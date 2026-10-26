// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Room creation and the member roster. The roster is built from live sessions in the
/// hub, so a second client that joins appears without any change here.
/// </summary>
public sealed class RoomHandlers(
    ProtocolTextPolicy textPolicy,
    Microsoft.Extensions.Options.IOptionsMonitor<ServerOptions> options,
    OpenEleven.Data.GameDbContext db,
    ILogger<RoomHandlers> log)
{
    private const ServiceRole RoomRoles = ServiceRole.Menu | ServiceRole.Lobby | ServiceRole.FdLobby;

    [Command("CMD_CREATEJOIN_ROOM", Roles = RoomRoles, RequiredState = SessionState.InBlock)]
    public ValueTask<KvMessage[]> CreateOrJoinRoom(CommandContext ctx)
    {
        // Keep accepting the older combined form used during early protocol tracing.
        // The room browser sends CMD_JOIN_ROOM for an advertised existing room.
        var requestedId = ctx.Request.GetInt32("room_id", 0);
        if (requestedId > 0)
            return ValueTask.FromResult(JoinExistingRoom(ctx, requestedId, includeRoomId: true));

        if (ctx.Session.RoomId is not null)
            return Reply.Of(ctx.Fail("ERR_ALREADYINROOM"));

        var maxPlayers = ctx.Request.GetInt32("max_players", 4);
        var requestedName = ctx.Request.GetString("name") ?? ServerText.Get("room.default", ctx.Session.Language, ctx.Session.PlayerName);
        if (ctx.Session is not { Pid: > 0, BlockId: not null }
            || maxPlayers is not (2 or 4)
            || !textPolicy.TryValidate(requestedName, out var name))
            return Reply.Of(ctx.Fail("ERR_INVALIDROOMINFO"));

        var room = ctx.Hub.CreateRoom(ctx.Session, name, maxPlayers);

        room.MatchType = ctx.Request.GetString("match_type") ?? "";
        room.TeamCategory = ctx.Request.GetString("team_category") ?? "";
        room.Language = ctx.Request.GetString("lang") ?? ctx.Session.Language;
        room.Password = ctx.Request.GetString("password") ?? "";
        room.AllowGuest = ctx.Request.GetString("enable_guest") != "NO";
        room.InviteLimited = ctx.Request.GetString("is_invite_limit") == "YES";
        room.TeamName = ctx.Request.GetString("team_name") ?? "";
        room.ShortTeamName = ctx.Request.GetString("short_teamname") ?? "";
        room.LegendsVsPlayer = ctx.Request.GetString("is_legends_vs_player") ?? "NO";
        // The client writes team_flag=["YES","YES","YES",]; the trailing comma can leave an empty entry.
        if (ctx.Request.GetValue("team_flag") is KvArray { } flags
            && flags.Values.OfType<string>().Where(f => f.Length > 0).ToArray() is { Length: 3 } chosen)
            room.TeamFlag = new KvArray(chosen);
        room.DlcVersion = ctx.Session.DlcVersion;

        ctx.Hub.PublishRoomUpdated(room, ctx.Session.Id);

        var created = ctx.Ok().Set("room_id", room.Id);
        if (!Is2010)
            return Reply.Of(created);

        // PES 2010 shows the room screen from its room-list cache (FUN_00759e50 by room_id).
        // The creator's cache has no entry for a room it just made, so without this its room
        // name is blank and "Start Match" stays disabled. (The owner's copy that the room menu
        // needs comes with CMD_SET_GAMEENV; see there.)
        return Reply.Of(created, ctx.Hub.RoomListUpdate(room));
    }

    private bool Is2010 => options.CurrentValue.GameProfile == GameProfile.Pes2010Pc;

    [Command("CMD_JOIN_ROOM", Roles = RoomRoles, RequiredState = SessionState.InBlock)]
    public ValueTask<KvMessage[]> JoinRoom(CommandContext ctx)
    {
        // PES2010 FUN_007B8AD0 sends room_id/is_invited/password and advances after the
        // ordinary command result; no room payload is parsed from the success response.
        var roomId = ctx.Request.GetInt32("room_id", 0);
        if (roomId <= 0)
            return Reply.Of(ctx.Fail("ERR_INVALIDROOMINFO"));

        return ValueTask.FromResult(JoinExistingRoom(ctx, roomId, includeRoomOwner: true));
    }

    /// <summary>
    /// Endpoint of another player, for the peer-to-peer connection the clients make
    /// between themselves.
    /// </summary>
    /// <remarks>
    /// Gated at InBlock, not InRoom: the client asks for the host's endpoint from the room
    /// browser, immediately before CMD_JOIN_ROOM, so requiring InRoom refused the very
    /// request that precedes joining. The answer is still confined to the caller's own
    /// block, so this exposes no endpoint the caller could not already see in the room and
    /// block player lists.
    /// </remarks>
    [Command("CMD_GET_IPANDPORT", Roles = RoomRoles, RequiredState = SessionState.InBlock)]
    public ValueTask<KvMessage[]> GetIpAndPort(CommandContext ctx)
    {
        // PES2010 FUN_0072B8A0 parses these five top-level fields.
        var requestedPid = ctx.Request.GetInt32("pid", 0);
        if (requestedPid <= 0 || ctx.Session.BlockId is not { } blockId)
            return Reply.Of(ctx.Fail("ERR_NOPLAYER"));

        // Prefer the caller's own room, so a member is resolved the same way as before
        // when the caller is already inside one; otherwise fall back to the block.
        var room = ctx.Session.RoomId is { } roomId ? ctx.Hub.FindRoom(roomId) : null;
        var member =
            room?.Snapshot().FirstOrDefault(candidate => candidate.Pid == requestedPid)
            ?? ctx.Hub.SessionsInBlock(blockId)
                .FirstOrDefault(candidate =>
                    candidate.Pid == requestedPid && candidate.Role == ctx.Session.Role);

        if (member is null)
            return Reply.Of(ctx.Fail("ERR_NOPLAYER"));

        var endpoints = ctx.Hub.EndpointsFor(ctx.Session, member);
        return Reply.Of(ctx.Ok()
            .Set("ex_ip", endpoints.ExternalIp)
            .Set("ex_port", endpoints.ExternalPort)
            .Set("in_ip", endpoints.InternalIp)
            .Set("in_port", endpoints.InternalPort)
            .Set("pid", member.Pid));
    }

    [Command("MSG_REQROOMLIST", Roles = RoomRoles, RequiredState = SessionState.InBlock)]
    public ValueTask<KvMessage[]> RequestRoomList(CommandContext ctx)
    {
        // FUN_00BB9200 requires ACK / START / LIST / END. It reads "roomList[N]" per
        // index, but that string is a path expression evaluated by FUN_00B6E0F0 —
        // list "roomList", element N — not a literal key, and FUN_00B6DD20 finds the
        // element by walking {...} groups. So the bracketed list is the form it reads.
        ctx.Session.RoomListSubscribed = true;
        var rooms = ctx.Hub.RoomsFor(ctx.Session)
            .Select(RoomPresenter.ListEntry)
            .ToArray();

        return Reply.Of(
            ctx.Ok(),
            ctx.Ok("MSG_ROOMLISTSTART"),
            ctx.Ok("MSG_ROOMLIST").SetList("count", "roomList", rooms),
            ctx.Ok("MSG_ROOMLISTEND"));
    }

    [Command("CMD_LEAVE_ROOM", Roles = RoomRoles, RequiredState = SessionState.InBlock)]
    public ValueTask<KvMessage[]> LeaveRoom(CommandContext ctx)
    {
        if (ctx.Session.RoomId is not { } roomId)
            return Reply.Of(ctx.Ok());

        var result = ctx.Hub.LeaveRoom(ctx.Session, roomId);
        return result.Status == RoomLeaveStatus.RoomNotFound
            ? Reply.Of(ctx.Fail("ERR_ROOMNOTFOUND"))
            : Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// Match settings chosen by the room owner, arriving as a nested record
    /// (<c>game_env={cpuLevel="HARD",gametime="5MINUTES",...}</c>). The client is happy
    /// with a bare NOERR, but the settings have to reach whoever joins the room, so they
    /// are held on the room rather than discarded.
    /// </summary>
    [Command("CMD_SET_GAMEENV", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> SetGameEnvironment(CommandContext ctx)
    {
        var environment = ctx.Request.GetValue("game_env") as KvMessage;
        if (environment is null)
        {
            log.LogWarning("CMD_SET_GAMEENV without a game_env record: {Request}", ctx.Request);
            return Reply.Of(ctx.Ok());
        }

        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Err("NOROOM"));

        room.GameEnvironment = environment;
        // The other occupants only ever learn the settings through this watch.
        ctx.Hub.PublishDecideGameEnv(room);
        ctx.Hub.PublishRoomUpdated(room, ctx.Session.Id);

        log.LogInformation(
            "Room {RoomId} settings: gametime={Time} cpu={Cpu} ball={Ball} subs={Subs}",
            room.Id,
            environment.GetString("gametime"),
            environment.GetString("cpuLevel"),
            environment.GetInt32("ball_type"),
            environment.GetInt32("substitution"));

        return Reply.Of(ctx.Ok());
    }

    [Command("CMD_WATCH_ENTRY_GAME", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> WatchEntryGame(CommandContext ctx)
    {
        ctx.Session.GameEntryWatchRqid = ctx.Rqid;
        if (ctx.Session.RoomId is { } roomId && ctx.Hub.FindRoom(roomId) is { } room)
            ctx.Hub.PublishGameEntryChanged(room);
        return Reply.None();
    }

    // Ghidra FUN_00754780 registers Lobby watches during service initialization,
    // before MSG_REQAUTH and CMD_SET_CURRENTPLAYER complete.
    [Command("CMD_WATCH_ROOMSTATE", Roles = RoomRoles, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> WatchRoomState(CommandContext ctx)
    {
        ctx.Session.RoomStateWatchRqid = ctx.Rqid;
        if (ctx.Session.RoomId is { } roomId && ctx.Hub.FindRoom(roomId) is { } room)
            ctx.Hub.PublishRoomStateChanged(room);
        return Reply.None();
    }

    [Command("CMD_WATCH_IPANDPORT", Roles = RoomRoles, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> WatchIpAndPort(CommandContext ctx)
    {
        ctx.Session.IpAndPortWatchRqid = ctx.Rqid;
        if (ctx.Session.RoomId is { } roomId && ctx.Hub.FindRoom(roomId) is { } room)
            ctx.Hub.PublishPeerEndpoints(room, ctx.Session);
        return Reply.None();
    }

    // FUN_007bacc0 arms the next six together when the entry-game screen opens, before
    // any of them has anything to report. Each one is how a member that is not the host
    // learns what the host is doing, so none may depend on a parked rqid.
    [Command("CMD_WATCH_DECIDE_GAMEENV", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> WatchDecideGameEnv(CommandContext ctx)
    {
        ctx.Session.DecideGameEnvWatchRqid = ctx.Rqid;
        if (ctx.Session.RoomId is { } roomId && ctx.Hub.FindRoom(roomId) is { } room)
            ctx.Hub.PublishDecideGameEnv(room);
        return Reply.None();
    }

    [Command("CMD_WATCH_DECIDE_GAMEPLAYER", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> WatchDecideGamePlayer(CommandContext ctx)
    {
        ctx.Session.DecideGamePlayerWatchRqid = ctx.Rqid;
        if (ctx.Session.RoomId is { } roomId && ctx.Hub.FindRoom(roomId) is { } room)
            ctx.Hub.PublishDecideGamePlayer(room);
        return Reply.None();
    }

    [Command("CMD_WATCH_DECIDE_GAMEPLAYERENV", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> WatchDecideGamePlayerEnv(CommandContext ctx)
    {
        ctx.Session.DecideGamePlayerEnvWatchRqid = ctx.Rqid;
        if (ctx.Session.RoomId is { } roomId && ctx.Hub.FindRoom(roomId) is { } room)
            ctx.Hub.PublishDecideGamePlayerEnv(room);
        return Reply.None();
    }

    [Command("CMD_WATCH_DISCON_PLAYERENV", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> WatchDisconPlayerEnv(CommandContext ctx)
    {
        // Nothing to report until somebody leaves.
        ctx.Session.DisconPlayerEnvWatchRqid = ctx.Rqid;
        return Reply.None();
    }

    [Command("CMD_WATCH_DISCON_PLAYERMATCH", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> WatchDisconPlayerMatch(CommandContext ctx)
    {
        ctx.Session.DisconPlayerMatchWatchRqid = ctx.Rqid;
        return Reply.None();
    }

    // Bare signal. Its trigger is the post-match record write, which this server does not
    // implement yet; registering it keeps the screen's arming burst from erroring.
    [Command("CMD_WATCH_UPDATE_GAMERECORD", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> WatchUpdateGameRecord(CommandContext ctx)
    {
        ctx.Session.UpdateGameRecordWatchRqid = ctx.Rqid;
        return Reply.None();
    }

    [Command("CMD_SET_GUEST", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> SetGuest(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_ROOMNOTFOUND"));

        ctx.Session.HasGuestPlayer = ctx.Request.GetString("has_guestplayer") == "YES";
        ctx.Hub.PublishRoomUpdated(room);
        return Reply.Of(ctx.Ok());
    }

    [Command("CMD_CHANGE_ROOMNAME", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> ChangeRoomName(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_ROOMNOTFOUND"));
        if (ctx.Session.Pid != room.OwnerPid)
            return Reply.Of(ctx.Fail("ERR_NOTROOMOWNER"));

        if (!textPolicy.TryValidate(ctx.Request.GetString("name") ?? "", out var name))
            return Reply.Of(ctx.Fail("ERR_INVALIDROOMINFO"));

        room.Name = name;
        room.Password = ctx.Request.GetString("passwd") ?? room.Password;
        ctx.Hub.PublishRoomUpdated(room);
        return Reply.Of(ctx.Ok());
    }

    [Command("CMD_KICK_ROOMMEMBER", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> KickRoomMember(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_ROOMNOTFOUND"));
        if (ctx.Session.Pid != room.OwnerPid)
            return Reply.Of(ctx.Fail("ERR_NOTROOMOWNER"));

        var targetPid = ctx.Request.GetInt32("target_pid", 0);
        var target = room.Snapshot().FirstOrDefault(member => member.Pid == targetPid);
        if (target is null || target.Pid == ctx.Session.Pid)
            return Reply.Of(ctx.Fail("ERR_TARGETPLAYERNOTEXIST"));

        // The kicked player has to be told, or its client stays in the room screen: the
        // room-state watch (parser FUN_0051c110) carries event enum 0x45, whose "Kicked"
        // takes the client out. Built before LeaveRoom, which drops the target's watch rqid.
        if (ctx.Hub.BuildRoomStateUpdate(room, target, "Kicked") is { } kicked)
            target.Push(kicked);

        // Same path as a voluntary exit, so the notices and any ownership handover match.
        ctx.Hub.LeaveRoom(target, room.Id);
        log.LogInformation(
            "pid {Target} kicked from room {RoomId} by owner pid {Owner}",
            targetPid, room.Id, ctx.Session.Pid);
        return Reply.Of(ctx.Ok());
    }

    // FUN_007c8500 sends this after CMD_GET_IPANDPORT/NAT negotiation. Despite
    // its WATCH name, this is a request/response gate: the client waits for the
    // same command with room_id before entering the next room setup state.
    [Command("CMD_WATCH_ROOMPLAYERLIST", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> WatchRoomPlayerList(CommandContext ctx)
    {
        var requestedRoomId = ctx.Request.GetInt32("room_id", 0);
        if (ctx.Session.RoomId is not { } roomId || requestedRoomId != roomId
            || ctx.Hub.FindRoom(roomId) is not { } room)
            return Reply.Of(ctx.Fail("ERR_ROOMNOTFOUND"));

        return Reply.Of(RoomPresenter.RoomPlayerWatch(room, ctx.Rqid));
    }

    [Command("CMD_UPDATE_ROOMSTATE", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> UpdateRoomState(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } roomId ? ctx.Hub.FindRoom(roomId) : null;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_ROOMNOTFOUND"));

        var status = ctx.Request.GetString("status") ?? "";
        if (!room.TrySetStatus(status))
        {
            log.LogWarning(
                "Invalid CMD_UPDATE_ROOMSTATE from pid {Pid}: status={Status}",
                ctx.Session.Pid,
                status);
            return Reply.Of(ctx.Fail("ERR_INVALIDROOMINFO"));
        }

        // Complete CMD_UPDATE_ROOMSTATE before satisfying the sender's parked watch.
        if (!ctx.Session.Push(ctx.Ok()))
            log.LogWarning(
                "Outbound queue full; dropped CMD_UPDATE_ROOMSTATE ACK for {Session}",
                ctx.Session);

        ctx.Hub.PublishRoomStateChanged(room);
        ctx.Hub.PublishRoomUpdated(room, ctx.Session.Id);
        log.LogInformation(
            "Room {RoomId} state changed to {Status} by pid {Pid}",
            room.Id,
            room.Status,
            ctx.Session.Pid);
        return Reply.None();
    }

    [Command("CMD_ENTRY_GAME", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> EntryGame(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_ROOMNOTFOUND"));

        // FUN_007b7bc0 writes entry as the numeric enum value 1. Keep the textual
        // aliases for captures/older clients, but do not silently drop the real wire
        // form: that leaves GameEntryNo at -1 and prevents the entry screen advancing.
        var entered = ctx.Request.GetString("entry") switch
        {
            "1" or "TRUE" or "YES" => true,
            "0" or "FALSE" or "NO" => false,
            _ => (bool?)null,
        };
        var side = ctx.Request.GetString("side") switch
        {
            "HOME" => 0,
            "AWAY" => 1,
            _ => -1,
        };

        if (entered is null || (entered.Value && side < 0))
        {
            log.LogWarning(
                "Invalid CMD_ENTRY_GAME from pid {Pid}: entry={Entry} side={Side}",
                ctx.Session.Pid,
                ctx.Request.GetString("entry"),
                ctx.Request.GetString("side"));
            return Reply.Of(ctx.Ok());
        }

        // pes2011.exe sends entry=FALSE for both players right after CMD_SET_GAMEMEMBERENV, as
        // it leaves the entry screen. Honouring it empties the entries and the clients then
        // report "communications with other users have been lost".
        if (room.MatchStarting && !entered.Value)
            return Reply.Of(ctx.Ok());

        room.SetGameEntry(ctx.Session, entered.Value, side);

        // Complete CMD_ENTRY_GAME before satisfying the sender's parked watch.
        if (!ctx.Session.Push(ctx.Ok()))
            log.LogWarning("Outbound queue full; dropped CMD_ENTRY_GAME ACK for {Session}", ctx.Session);

        // Entering changes the roster and the side assignments, which the entry-game screen
        // tracks through three separate watches.
        var watchRecipients = ctx.Hub.PublishGameEntryChanged(room);
        ctx.Hub.PublishDecideGamePlayer(room);
        ctx.Hub.PublishDecideGamePlayerEnv(room);
        ctx.Hub.PublishRoomUpdated(room);
        log.LogInformation(
            "pid {Pid} game entry={Entered} side={Side} room={RoomId}; watches={Watches}",
            ctx.Session.Pid, entered.Value, side, room.Id, watchRecipients);

        return Reply.None();
    }

    [Command("MSG_REQRMEMBERLIST", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> RequestRoomMemberList(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Err("NOROOM"));

        return Reply.Of(
        [
            ctx.Ok(),
            .. RoomPresenter.MemberListStream(room, ctx.Rqid),
        ]);
    }

    private static KvMessage RoomJoinError(CommandContext ctx, RoomJoinStatus status)
        => status switch
        {
            RoomJoinStatus.RoomNotFound => ctx.Fail("ERR_ROOMNOTFOUND"),
            RoomJoinStatus.Full => ctx.Fail("ERR_ROOMISFULL"),
            RoomJoinStatus.AlreadyInRoom => ctx.Fail("ERR_ALREADYINROOM"),
            RoomJoinStatus.InvalidRoomInfo or RoomJoinStatus.WrongPassword
                => ctx.Fail("ERR_INVALIDROOMINFO"),
            _ => ctx.Fail("ERR_INVALIDROOMINFO"),
        };

    private KvMessage[] JoinExistingRoom(
        CommandContext ctx,
        int roomId,
        bool includeRoomId = false,
        bool includeRoomOwner = false)
    {
        if (ctx.Hub.FindRoom(roomId) is { } target
            && target.Snapshot().FirstOrDefault(m => m.Pid == target.OwnerPid) is { } roomOwner)
        {
            // Modified game data (a different para_hash from login) must not mix with the
            // owner's: ERR_DIFFDCVERSION is the "different data version" refusal the client knows.
            if (options.CurrentValue.Protocol.RequireSameParaHashToJoin
                && roomOwner.ParaHash.Length > 0 && ctx.Session.ParaHash.Length > 0
                && !string.Equals(roomOwner.ParaHash, ctx.Session.ParaHash, StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("pid {Pid} refused from room {RoomId}: para_hash {Hash} differs from the owner's {OwnerHash}",
                    ctx.Session.Pid, roomId, ctx.Session.ParaHash, roomOwner.ParaHash);
                return [ctx.Fail("ERR_DIFFDCVERSION")];
            }

            // A blacklisted player sees the room as full rather than learning it was blocked.
            if (db.Blacklist.Any(b => b.Pid == roomOwner.Pid && b.TargetPid == ctx.Session.Pid))
                return [ctx.Fail("ERR_ROOMISFULL")];
        }

        var joined = ctx.Hub.JoinRoom(
            ctx.Session,
            roomId,
            ctx.Request.GetString("password") ?? "");
        if (joined.Status != RoomJoinStatus.Joined || joined.Room is null)
            return [RoomJoinError(ctx, joined.Status)];

        ctx.Hub.PublishRoomJoined(joined, ctx.Session);
        log.LogInformation("pid {Pid} joined room {RoomId}", ctx.Session.Pid, joined.Room.Id);
        var response = ctx.Ok();
        if (includeRoomId)
            response.Set("room_id", joined.Room.Id);

        if (includeRoomOwner)
        {
            // PES2010 FUN_00730ED0 stores room_owner.pid as the target for the immediate
            // CMD_GET_IPANDPORT request. Missing this object produces the observed pid=0.
            var owner = joined.Room.Snapshot()
                .First(member => member.Pid == joined.Room.OwnerPid);
            var endpoints = ctx.Hub.EndpointsFor(ctx.Session, owner);
            response.Set("room_owner", new KvMessage()
                .Set("name", owner.PlayerName)
                .Set("pid", owner.Pid)
                .Set("xuid", 0L)
                .Set("ex_ip", endpoints.ExternalIp)
                .Set("ex_port", endpoints.ExternalPort)
                .Set("in_ip", endpoints.InternalIp)
                .Set("in_port", endpoints.InternalPort));
        }

        var replies = new List<KvMessage> { response };
        // The PES2010 client arms CMD_WATCH_ROOMSTATE locally during service init,
        // so the join response must include the current state even without a parked
        // server-side rqid. Keep a supplied rqid for protocol variants that do park it.
        replies.Add(ctx.Hub.BuildRoomStateUpdate(joined.Room, ctx.Session)!);

        // Queue the roster after the CMD_JOIN_ROOM response, never before it: the join
        // state machine is waiting on that response, and the roster stream is also what
        // populates the room screen for a player who joined an existing room.
        replies.AddRange(RoomPresenter.MemberListStream(joined.Room, Hub.UnsolicitedRqid));
        // PES 2010 looks the host up in this list before connecting to it (FUN_007b8ad0).
        if (options.CurrentValue.GameProfile != GameProfile.Pes2011Pc)
            replies.Add(RoomPresenter.RoomPlayerWatch(joined.Room, Hub.UnsolicitedRqid));

        // PublishRoomJoined skips the joiner; it gets its own copy here, after the join response.
        if (ctx.Hub.RoomListUpdateFor(ctx.Session, joined.Room) is { } update)
            replies.Add(update);
        return replies.ToArray();
    }
}
