// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// PES 2011 (PC) discovery and login.
/// </summary>
/// <remarks>
/// Everything here is gated to <see cref="GameProfile.Pes2011Pc"/>; the shared handlers keep
/// serving the other titles, and the two implementations are selected by
/// <see cref="CommandAttribute.Profiles"/> rather than by a runtime branch.
/// Function addresses refer to <c>pes2011.exe</c> (PE32, image base 0x00400000, Ghidra
/// language <c>x86:LE:32:default</c>) and are recorded with their evidence in
/// <c>docs/reverse-engineering/pes2011-session-protocol.md</c>.
/// The transport is *not* re-implemented here: PES 2011 uses the same 24-byte frame, XOR
/// keystream and Blowfish key as PES 2010 (see <c>FUN_01006660</c>, <c>FUN_00feba00</c>).
/// </remarks>
public sealed class Pes2011Handlers(
    ServerCatalog catalog,
    GameIdTicketStore tickets,
    IAccountRepository accounts,
    IPlayerRepository players,
    IMatchRepository matches,
    MatchResults matchResults,
    Microsoft.Extensions.Options.IOptionsMonitor<ServerOptions> options,
    Web.WebAssets webAssets,
    ILogger<Pes2011Handlers> log)
{
    /// <summary>The services the login handshake is known to run on.</summary>
    private const ServiceRole LoginRoles = ServiceRole.Gate | ServiceRole.Account | ServiceRole.Menu;

    /// <summary>
    /// The first command on every new game socket, sent right after <c>connect()</c> and before
    /// any gate or account traffic: <c>FUN_0050d880</c> registers the name with
    /// <c>FUN_0051fc00("NETCLIENT")</c>, and <c>FUN_005228a0</c> only advances its state
    /// machine once the reply arrives. Empty body; a bare NOERR envelope is the whole contract.
    /// </summary>
    [Command("NETCLIENT", Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> NetClient(CommandContext ctx) => Reply.Of(ctx.Ok());

    /// <summary>
    /// The client's self-registration: <c>FUN_00512320</c> builds
    /// <c>server_num=1,server_list=[{svrtype="GATE",svraddr="pes11pcgate-e.winning-eleven.net",
    /// svrport=4010,userCount={now=1,min=0,max=10}}]</c> (the port comes from the immediate
    /// <c>0xfaa</c> in that function) and expects a bare acknowledgement. The advertised
    /// address is the client's own idea of where the gate lives, so it is logged and not stored.
    /// </summary>
    [Command("CMD_REG_SVRLIST", Roles = ServiceRole.Gate, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> RegisterServerList(CommandContext ctx)
    {
        log.LogDebug(
            "PES 2011 gate self-registration from {Session}: svrtype={SvrType}",
            ctx.Session, ctx.Request.GetString("svrtype"));

        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// The server-selection screen. The response parser (<c>FUN_005ef7c0</c>) reads
    /// <c>lobby_list_num</c> and then <c>lobby_list[i].lobby_id|name|player_num</c>; with none of
    /// those the screen is simply empty. Each entry is a configured Lobby service (its Gid is the
    /// lobby_id); the blocks inside it are what CMD_GET_BLOCKLIST lists, so its player count is
    /// everyone in any block. The request's <c>lobby_type</c> ("OC" in a live run) is not interpreted.
    /// </summary>
    [Command("CMD_GET_LOBBYLIST", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    public ValueTask<KvMessage[]> GetLobbyList(CommandContext ctx)
    {
        var playerNum = ctx.Hub.PlayersInBlocks;
        var entries = LobbyServices.Select(s => new KvMessage()
            .Set("lobby_id", s.Gid)
            .Set("name", s.Name)
            .Set("player_num", playerNum)).ToArray();

        return Reply.Of(ctx.Ok().SetList("lobby_list_num", "lobby_list", entries));
    }

    private IEnumerable<ServiceEndpointOptions> LobbyServices
        => catalog.Endpoints.Where(s => s.Role == ServiceRole.Lobby).OrderBy(s => s.Gid);

    /// <summary>
    /// Block roster / room list subscriptions, sent as a pair right after <c>CMD_JOIN_BLOCK</c>
    /// (<c>FUN_005f6f80</c>). The request names the keys it wants back
    /// (<c>listnum_name</c>, <c>list_name</c>, e.g. <c>blockplayer_list_num</c> /
    /// <c>blockplayer_list</c>); a bare acknowledgement leaves the watcher task in its error
    /// state and the client leaves the lobby with CMD_LEAVE_LOBBY. Answered with an empty list
    /// under exactly those keys until live rosters are pushed.
    /// </summary>
    [Command("CMD_REQWATCHL_BLOCKPLAYERLIST", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    public async ValueTask<KvMessage[]> WatchBlockPlayers(CommandContext ctx)
    {
        ctx.Session.BlockPlayerWatch = Subscription(ctx, "blockplayer_list_num", "blockplayer_list");

        // One entry per player in the block, the same record later ADD pushes carry.
        var entries = new List<KvMessage>();
        foreach (var pid in ctx.Hub.SessionsInBlock(ctx.Session.BlockId ?? 0)
                     .Where(s => s.Role == ctx.Session.Role && s.Pid > 0).Select(s => s.Pid).Distinct().Take(100))
        {
            var player = await players.GetAsync(pid, ctx.CancellationToken);
            var inRoom = ctx.Hub.SessionsForPid(pid).Select(s => s.RoomId).FirstOrDefault(r => r is not null) ?? 0;
            if (player is not null)
                entries.Add(Pes2011Lists.BlockPlayerEntry(PlayerPresenter.BlockListEntry(player, inRoom)));
        }

        return [WatchReply(ctx, ctx.Session.BlockPlayerWatch, entries)];
    }

    [Command("CMD_REQWATCHL_ROOMLIST", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    public ValueTask<KvMessage[]> WatchRooms(CommandContext ctx)
    {
        ctx.Session.RoomListWatch = Subscription(ctx, "room_list_num", "room_list");
        var entries = ctx.Hub.RoomsFor(ctx.Session).Take(100).Select(Pes2011Lists.RoomEntry).ToArray();
        return ValueTask.FromResult<KvMessage[]>([WatchReply(ctx, ctx.Session.RoomListWatch, entries)]);
    }

    private static WatchListSubscription Subscription(CommandContext ctx, string numDefault, string listDefault)
        => new(ctx.Request.MsgName ?? "", ctx.Rqid,
            ctx.Request.GetString("listnum_name") ?? numDefault,
            ctx.Request.GetString("list_name") ?? listDefault);

    private static KvMessage WatchReply(CommandContext ctx, WatchListSubscription sub, IReadOnlyList<KvMessage> entries)
        => ctx.Ok().Set("list_type", "ADD").SetList(sub.NumName, sub.ListName, entries);

    /// <summary>
    /// The client's own STUN result (pes2011.exe sends it right after CMD_SET_CURRENTPLAYER).
    /// Peers are told these endpoints in CMD_JOIN_ROOM / CMD_GET_IPANDPORT, so without them
    /// every member looks like 127.0.0.1:0 and the P2P connection to the host never starts.
    /// </summary>
    [Command("CMD_SEND_IPANDPORT", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    public ValueTask<KvMessage[]> SendIpAndPort(CommandContext ctx)
    {
        var s = ctx.Session;
        s.ExternalIp = ctx.Request.GetString("ex_ip") ?? s.ExternalIp;
        s.ExternalPort = ctx.Request.GetInt32("ex_port", s.ExternalPort);
        s.InternalIp = ctx.Request.GetString("in_ip") ?? s.InternalIp;
        s.InternalPort = ctx.Request.GetInt32("in_port", s.InternalPort);
        log.LogInformation("pid {Pid} endpoints external={ExIp}:{ExPort} internal={InIp}:{InPort} nat={Nat}",
            s.Pid, s.ExternalIp, s.ExternalPort, s.InternalIp, s.InternalPort, ctx.Request.GetString("nat"));
        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// The host fixes who plays which side (pes2011.exe FUN_0065a7d0 state 3, request
    /// <c>envlist[]{pid,side,sideLeader}</c>). The match is now starting: the room moves to GAME.
    /// </summary>
    [Command("CMD_SET_GAMEMEMBERENV", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> SetGameMemberEnv(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_NOTINROOM"));

        foreach (var entry in ctx.Request.GetList("envlist"))
        {
            var member = room.Snapshot().FirstOrDefault(m => m.Pid == entry.GetInt32("pid", -1));
            if (member is not null)
                member.GameSide = entry.GetString("side") == "AWAY" ? 1 : 0;
        }

        // The room stays in its current state: CMD_CHANGE_GAMEPHASE is what moves it to GAME
        // (ntlserver, the PES 2013 server, does the same and only refreshes the roster here).
        room.MatchStarting = true;
        ctx.Hub.PublishRoomPlayers(room);
        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// The match host reports each phase (enum 0x1a: ENTRY, 1ST, BREAK1 ... PK, END). The room
    /// status (enum 0x44) has to follow: in-match FSM FUN_005fa030 ends the match only when a
    /// CMD_WATCH_ROOMSTATE says WAITING, and FUN_005f9ec0 asks for CMD_GET_GAMEADDPOINT on RESULT.
    /// </summary>
    [Command("CMD_CHANGE_GAMEPHASE", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.InRoom)]
    public async ValueTask<KvMessage[]> ChangeGamePhase(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];
        if (!IsMatchHost(ctx, room))
            return [ctx.Ok()];

        var phase = ctx.Request.GetString("phase") ?? "";
        if (phase == "1ST")
        {
            (room.HomeGoals, room.AwayGoals) = (0, 0);
            room.Sheet = new MatchSheet();
            room.ResultRecorded = false;
            room.ForfeitStayerPid = null;
            room.LastResults = new Dictionary<int, PlayerResult>();
        }

        room.Phase = phase;
        if (phase is "EX1ST" or "EX2ND")
            room.Sheet.ReachedExtraTime = true;
        if (phase == "PK")
            room.Sheet.ReachedPenalties = true;

        if (phase == "END")
        {
            // Settled before RESULT goes out: both clients ask CMD_GET_GAMEADDPOINT right after it.
            await matchResults.RecordAsync(room, MatchResults.Lineup(room), leaverPid: null, noMatch: false);
        }

        var status = phase switch
        {
            "1ST" => "GAME",
            "END" => "RESULT",
            _ => room.Status,
        };
        return await AckThenSetStatus(ctx, room, status);
    }

    /// <summary>The match host's team pick (FUN_005fa030 event 5), kept for the match history.</summary>
    [Command("CMD_SET_GAMETEAM", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> SetGameTeam(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_NOTINROOM"));

        room.HomeTeamId = ctx.Request.GetInt32("home_team_id", 0);
        room.AwayTeamId = ctx.Request.GetInt32("away_team_id", 0);
        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// The match host reports each goal (FUN_005fa030 event 3): goal_side HOME|AWAY, the scorer's
    /// goal_gameplayer_id / goal_player_pid and the assist. Kept for the match result.
    /// </summary>
    [Command("CMD_ADD_SCORE", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> AddScore(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_NOTINROOM"));
        if (!IsMatchHost(ctx, room))
            return Reply.Of(ctx.Ok());

        var away = ctx.Request.GetString("goal_side") == "AWAY";
        // Shoot-out goals are kept apart: they decide a draw but are not part of the score.
        if (MatchSheet.PeriodOf(room.Phase) != 4)
        {
            if (away)
                room.AwayGoals++;
            else
                room.HomeGoals++;
        }

        var r = ctx.Request;
        room.Sheet.Goal(away, room.Phase, string.Join('|',
            r.GetInt32("goal_player_pid", 0), r.GetInt32("goal_gameplayer_id", 0), r.GetString("goal_player_side") ?? "",
            r.GetString("assist_side") ?? "", r.GetInt32("assist_gameplayer_id", 0), r.GetString("assist_player_side") ?? ""));

        log.LogInformation("Room {RoomId} goal by pid {Pid} ({Phase}): {Home}-{Away}",
            room.Id, r.GetInt32("goal_player_pid", 0), room.Phase, room.HomeGoals, room.AwayGoals);
        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// A card or an injury (FUN_005fa030): card_pid/card_side/card_gameplayer_id/card_kind
    /// (YELLOW|RED|NONE) and injury_side/injury_gameplayer_id. Kept with the match record.
    /// </summary>
    [Command("CMD_ADD_FOUL", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> AddFoul(CommandContext ctx)
    {
        if (ctx.Session.RoomId is { } id && ctx.Hub.FindRoom(id) is { } room && IsMatchHost(ctx, room))
        {
            var r = ctx.Request;
            if (r.GetString("card_kind") is { } kind and not "NONE")
                room.Sheet.Event(string.Join('|', "CARD", room.Phase, r.GetString("card_side") ?? "", kind,
                    r.GetInt32("card_pid", 0), r.GetInt32("card_gameplayer_id", 0)));
            if (r.GetString("injury_side") is { Length: > 0 } injurySide)
                room.Sheet.Event(string.Join('|', "INJURY", room.Phase, injurySide, r.GetInt32("injury_gameplayer_id", 0)));
        }

        return Reply.Of(ctx.Ok());
    }

    /// <summary>The host's combination play for the match (total_comb, max_comb); credited to the host.</summary>
    [Command("CMD_UPDATE_COMBINATION", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> UpdateCombination(CommandContext ctx)
    {
        if (ctx.Session.RoomId is { } id && ctx.Hub.FindRoom(id) is { } room && IsMatchHost(ctx, room))
        {
            var (total, max) = (ctx.Request.GetInt32("total_comb", 0), ctx.Request.GetInt32("max_comb", 0));
            room.Sheet.Combination(ctx.Session.Pid, total, max);
            room.TotalCombination = Math.Max(room.TotalCombination, total);
            room.MaxCombination = Math.Max(room.MaxCombination, max);
        }

        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// Match events (phase, goals, fouls, combinations) count only from the match host — the room
    /// owner, who also sends CMD_SET_GAMEMEMBERENV — and only once that has started the match.
    /// Live captures show the other client never sends them, so anything else is ignored (with
    /// a plain NOERR, which keeps a confused client on its feet) rather than trusted.
    /// </summary>
    private bool IsMatchHost(CommandContext ctx, Room room)
    {
        if (room.OwnerPid == ctx.Session.Pid && room.MatchStarting)
            return true;

        log.LogWarning("Ignored {Msg} from pid {Pid} in room {RoomId}: not the match host during a match (owner {Owner}, started {Started})",
            ctx.Msg, ctx.Session.Pid, room.Id, room.OwnerPid, room.MatchStarting);
        return false;
    }

    /// <summary>
    /// A player whose peer vanished declares the match forfeited (FUN_005fa030 event 9). Nothing
    /// else will end the match for it: the room goes back to WAITING, which FUN_005fa030 treats
    /// as "match over" and leaves the in-match screen.
    /// </summary>
    [Command("CMD_SET_FORFEITEDGAMEEND", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.InRoom)]
    public async ValueTask<KvMessage[]> SetForfeitedGameEnd(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];

        log.LogInformation("pid {Pid} forfeited the match in room {RoomId}", ctx.Session.Pid, room.Id);

        // The sender is the one left behind. Whether the other quit or the P2P link just broke
        // shows in the next seconds: its connection drops (Hub.LeaveRoom) or it does not.
        if (room.Status == "GAME")
            matchResults.Forfeited(room, ctx.Session.Pid);

        return await AckThenSetStatus(ctx, room, "WAITING");
    }

    /// <summary>
    /// The match host ends the match because a side ran out of players (FUN_005fa030 event 1,
    /// <c>side</c> = the side that lost). Same outcome as phase END: the room shows the result.
    /// </summary>
    [Command("CMD_SET_INJURYGAMEEND", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.InRoom)]
    public async ValueTask<KvMessage[]> SetInjuryGameEnd(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];

        await matchResults.RecordAsync(room, MatchResults.Lineup(room), leaverPid: null, noMatch: false);
        return await AckThenSetStatus(ctx, room, "RESULT");
    }

    /// <summary>
    /// The match broke down (FUN_005fa030 events 8/10, <c>is_sync_failed</c> YES|NO). Nothing to
    /// score: the room goes back to WAITING, which takes every client out of the match.
    /// </summary>
    [Command("CMD_SEND_ABNORMALEND", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.InRoom)]
    public async ValueTask<KvMessage[]> SendAbnormalEnd(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];

        log.LogInformation("pid {Pid} reported an abnormal end in room {RoomId} (sync failed: {Sync})",
            ctx.Session.Pid, room.Id, ctx.Request.GetString("is_sync_failed"));
        if (room.Status == "GAME")
            await matchResults.RecordAsync(room, MatchResults.Lineup(room), leaverPid: null, noMatch: true);

        return await AckThenSetStatus(ctx, room, "WAITING");
    }

    /// <summary>
    /// Asked by every player once the room reaches RESULT (FUN_005f9ec0); FUN_005405c0 reads
    /// <c>player_list[i].pid|addpoint|point|rating|oldrating|vs_count</c>, all optional.
    /// </summary>
    [Command("CMD_GET_GAMEADDPOINT", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.InRoom)]
    public async ValueTask<KvMessage[]> GetGameAddPoint(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];

        // What MatchResults settled at phase END; a player it has nothing for keeps its values.
        var list = new List<KvMessage>();
        foreach (var member in room.Snapshot())
        {
            if (!room.LastResults.TryGetValue(member.Pid, out var r))
            {
                var player = await players.GetAsync(member.Pid, ctx.CancellationToken);
                r = new PlayerResult(0, player?.Point ?? 0, player?.Rating ?? 0, player?.Rating ?? 0,
                    player?.Stats.MatchCount ?? 0);
            }

            list.Add(new KvMessage()
                .Set("pid", member.Pid)
                .Set("addpoint", r.AddPoint)
                .Set("point", r.Point)
                .Set("rating", r.Rating)
                .Set("oldrating", r.OldRating)
                .Set("vs_count", r.MatchCount));
        }

        return [ctx.Ok().SetList("player_count", "player_list", list)];
    }

    /// <summary>
    /// Match history of <c>pid</c> (FUN_00624790; reply parsed by FUN_00623420). Per entry:
    /// <c>rlist[i].date</c> (unix seconds, FUN_00660320 gmtime), <c>mode</c> (enum 0x25),
    /// <c>ex_type|pk_flag|no_match</c> (YES/NO), <c>score[s].score_1st|_2nd|_ex1st|_ex2nd|_pk</c>
    /// and <c>team[s]</c> for s = HOME, AWAY, then <c>player[k].name|manner|pid|has_guestplayer|
    /// division|rating|side</c> until a name is missing.
    /// </summary>
    [Command("CMD_GET_GAMERESULTS", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    public async ValueTask<KvMessage[]> GetGameResults(CommandContext ctx)
    {
        var pid = ctx.Request.GetInt32("pid", ctx.Session.Pid);
        var entries = new List<KvMessage>();
        foreach (var m in await matches.GetRecentAsync(pid, options.CurrentValue.Rank.HistoryLimit, ctx.CancellationToken))
        {
            var sides = new List<KvMessage>();
            foreach (var (side, who) in new[] { ("HOME", m.HomePid), ("AWAY", m.AwayPid) })
            {
                var p = await players.GetAsync(who, ctx.CancellationToken);
                if (p is not null)
                    sides.Add(new KvMessage()
                        .Set("name", p.Name)
                        .Set("manner", p.Manner)
                        .Set("pid", p.Pid)
                        .Set("has_guestplayer", "NO")
                        .Set("division", RankSystem.DivisionName(p.Division))
                        .Set("rating", p.Rating)
                        .Set("side", side));
            }

            // Records from before periods were kept carry the whole score in the first half.
            KvMessage Score(string periods, int total)
            {
                var p = periods.Length > 0 ? MatchSheet.Parse(periods) : [total, 0, 0, 0, 0];
                return new KvMessage()
                    .Set("score_1st", p[0]).Set("score_2nd", p[1]).Set("score_ex1st", p[2])
                    .Set("score_ex2nd", p[3]).Set("score_pk", p[4]);
            }

            // mode is enum 0x25: RANK_MATCH | COMPE_MATCH; every match played here is the former.
            entries.Add(new KvMessage()
                .Set("date", new DateTimeOffset(DateTime.SpecifyKind(m.PlayedAt, DateTimeKind.Utc)).ToUnixTimeSeconds())
                .Set("mode", "RANK_MATCH")
                .Set("ex_type", m.ExtraTime ? "YES" : "NO")
                .Set("pk_flag", m.Penalties ? "YES" : "NO")
                .Set("no_match", m.Disconnected ? "YES" : "NO")
                .Set("score", new[] { Score(m.HomePeriods, m.HomeScore), Score(m.AwayPeriods, m.AwayScore) })
                .Set("team", new KvArray([m.HomeTeamId, m.AwayTeamId]))
                .Set("player", sides));
        }

        return [ctx.Ok().SetList("rlist_num", "rlist", entries)];
    }

    /// <summary>
    /// Entering the online lobby (FUN_005f0030 sends <c>lobby_id</c> from CMD_GET_LOBBYLIST). The
    /// client only waits for the reply; ERR_LOBBYUSERMAX is the one refusal it knows.
    /// </summary>
    [Command("CMD_JOIN_LOBBY", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    public ValueTask<KvMessage[]> JoinLobby(CommandContext ctx)
    {
        var lobbyId = ctx.Request.GetInt32("lobby_id", 0);
        var lobby = LobbyServices.FirstOrDefault(s => s.Gid == lobbyId) ?? LobbyServices.FirstOrDefault();
        if (lobby is not null && lobby.MaxPlayers > 0 && ctx.Hub.PlayersInBlocks >= lobby.MaxPlayers)
            return Reply.Of(ctx.Fail("ERR_LOBBYUSERMAX"));

        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// Leaving the online lobby (FUN_005efde0). The player drops out of its room and block, so
    /// the others' lists lose it instead of keeping a ghost entry.
    /// </summary>
    [Command("CMD_LEAVE_LOBBY", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    public ValueTask<KvMessage[]> LeaveLobby(CommandContext ctx)
    {
        var s = ctx.Session;
        if (s.RoomId is { } roomId)
            ctx.Hub.LeaveRoom(s, roomId);

        if (s.BlockId is not null)
        {
            ctx.Hub.PublishBlockPlayerRemoved(s);
            s.BlockId = null;
            s.RoomListWatch = s.BlockPlayerWatch = null;
            if (s.State > SessionState.PlayerSelected)
                s.State = SessionState.PlayerSelected;
        }

        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// In-match reports the client only needs acknowledged (FUN_005fa030 and the match-room FSMs):
    /// fouls and injuries, combination stats, match info, the reply to a forfeit notice, the game
    /// member list (FUN_0065a7d0 only checks NOERR; the member payload FUN_005eb880 reads belongs
    /// to quick match) and P2P status.
    /// </summary>
    [Command("CMD_SET_COMMU_MATCHINFO", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    [Command("CMD_SEND_ML_GAMEPLAYERS", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    [Command("CMD_REQ_FORFEITEDGAME_REPLY", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    [Command("CMD_SET_GAMEMEMBER", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    [Command("CMD_SEND_P2PSTATUS", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    public ValueTask<KvMessage[]> MatchAck(CommandContext ctx) => Reply.Of(ctx.Ok());

    /// <summary>The reply goes first: the sender's state machine is waiting on it, not on the push.</summary>
    private static ValueTask<KvMessage[]> AckThenSetStatus(CommandContext ctx, Room room, string status)
    {
        ctx.Session.Push(ctx.Ok());
        if (room.Status != status && ctx.Hub.SetRoomStatus(room, status))
            ctx.Hub.PublishRoomUpdated(room);

        return Reply.None();
    }

    /// <summary>
    /// Both players ask for the id as the match is being set up (pes2011.exe FUN_005f9660,
    /// reply parsed by FUN_005f9350 into <c>match_id</c>); it has to be the same for both.
    /// It is the id of the match record, opened here, so it is stable across restarts.
    /// </summary>
    [Command("CMD_GET_MATCHID", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    public async ValueTask<KvMessage[]> GetMatchId(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];

        return [ctx.Ok().Set("match_id", await matchResults.StartAsync(room))];
    }

    /// <summary>
    /// Roster of the room the client asked about (pes2011.exe FUN_006407f0 reads
    /// <c>roomplayer_list[i].pid|room_player_id|room_entry_no|game_entry_no|desiredPosition[0..12]|gameplayer_name</c>,
    /// at most 8 entries).
    /// </summary>
    [Command("CMD_REQWATCHL_ROOMPLAYERLIST", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    public ValueTask<KvMessage[]> WatchRoomPlayers(CommandContext ctx)
    {
        var room = ctx.Hub.FindRoom(ctx.Request.GetInt32("room_id", ctx.Session.RoomId ?? 0));
        ctx.Session.RoomPlayerWatch = Subscription(ctx, "roomplayer_list_num", "roomplayer_list");
        var entries = ctx.Hub.RoomPlayerEntries(room, ctx.Session);
        return Reply.Of(WatchReply(ctx, ctx.Session.RoomPlayerWatch, entries));
    }

    // FUN_011d63a0 turns the reply into ServerResultErr unless it carries a list_type string;
    // "DEL" means entries are removed, anything else is an add/update.
    private static ValueTask<KvMessage[]> WatchList(CommandContext ctx)
        => Reply.Of(ctx.Ok().Set("list_type", "ADD").SetList(
            ctx.Request.GetString("listnum_name") ?? "list_num",
            ctx.Request.GetString("list_name") ?? "list",
            Array.Empty<KvMessage>()));

    /// <summary>
    /// The server list the client uses to find ACCOUNT/MENU. The response parser
    /// (<c>FUN_00ffd160</c>) addresses entries as <c>server_list[i].svrtype|svraddr|svrport|userCount</c>
    /// (format literal <c>server_list[%d].%s</c> at 0x013ba1a4) and raises
    /// <c>ERR_CLIENT_OPTFORMAT</c> for any entry missing one of those four fields, so the
    /// payload carries exactly those and nothing else.
    /// </summary>
    [Command("CMD_GET_SVRLIST", Roles = ServiceRole.Gate, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> GetServerList(CommandContext ctx)
    {
        var entries = catalog.BuildServerEntries();
        if (entries.Count == 0)
        {
            // The client's own canned failure shape: 0x013ba3a0 holds
            // result="%s",server_num=0,server_list=[] and SvrTypeErr is the only gate-layer
            // error literal in the binary.
            log.LogError(
                "PES 2011 CMD_GET_SVRLIST has nothing to advertise: no enabled endpoint maps to " +
                "an svrtype the client's parser recognises");

            return Reply.Of(new KvMessage()
                .Set("result", "SvrTypeErr")
                .Set("msg", ctx.Msg)
                .Set("rqid", ctx.Rqid)
                .SetList("server_num", catalog.ServerListKey, Array.Empty<KvMessage>()));
        }

        // The gate state machine (pes2011.exe FUN_0061e620, state 0x11) re-parses this same
        // reply with FUN_005104f0, which reads the PES 2010 shape (svrlist + svrport/max_player_num/
        // player_num/svrgid). A reply without it leaves the gate's +0x68 flag at 0, and state
        // 0x12 then ends the session through the error path ("An error has occurred"). So carry
        // both lists; each parser ignores the other's key.
        var legacy = catalog.BuildLegacyEntries();
        return Reply.Of(ctx.Ok()
            .SetList("server_num", catalog.ServerListKey, entries)
            .Set("svrlist", legacy));
    }

    /// <summary>
    /// The terms screen (FUN_00604880) asks for the EULA_PES url. The retail client turns this
    /// command into its own HTTP GET (request object FUN_011beb10) and wraps the body into
    /// <c>read_buffer</c>, so the /eula_pes.txt endpoint is what really answers; this socket
    /// handler gives the same text for a client that does send it here.
    /// </summary>
    [Command("CMD_GET_HTTP", Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> GetHttp(CommandContext ctx)
    {
        var uri = ctx.Request.GetString("uri") ?? "";
        var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : uri;

        string text;
        if (path.EndsWith("/eula_pes.txt", StringComparison.OrdinalIgnoreCase) || path.Length == 0)
        {
            var lang = parsed is null ? null : System.Web.HttpUtility.ParseQueryString(parsed.Query)["lang"];
            text = Web.GameEndpoints.EulaText(options.CurrentValue, lang ?? ctx.Session.Language);
        }
        else
        {
            text = webAssets.TryGet(path, out var content) ? System.Text.Encoding.UTF8.GetString(content) : "";
        }

        log.LogInformation("CMD_GET_HTTP uri {Uri}: {Length} characters", uri, text.Length);
        return Reply.Of(ctx.Ok().Set("read_buffer", text));
    }

    /// <summary>
    /// Stage one of the login: trade <c>gameid</c>/<c>gameid_password</c> for an opaque
    /// <c>loginid</c>/<c>loginid_password</c> pair (<c>FUN_0061c360</c> state 2, sender
    /// <c>FUN_006425c0</c>). Field names are verified; <c>password_type</c> carries
    /// <c>PASS_PLAIN</c> or <c>PASS_AES</c>.
    /// </summary>
    /// <remarks>
    /// <c>PASS_PLAIN</c> is checked against the account's registered digest, the same one the
    /// web registration form stores (<c>AuthProof.HashPassword</c>). <c>PASS_AES</c> is refused
    /// rather than guessed: the key that would unwrap <c>gameid_password</c> has not been
    /// recovered, so accepting the value would mean trusting a ciphertext as a password.
    /// </remarks>
    [Command("CMD_AUTH_GAMEID", Roles = LoginRoles, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.Connected)]
    public async ValueTask<KvMessage[]> AuthGameId(CommandContext ctx)
    {
        var gameId = ctx.Request.GetString("gameid")?.Trim();
        var password = ctx.Request.GetString("gameid_password");
        var passwordType = ctx.Request.GetString("password_type") ?? "PASS_PLAIN";

        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(password))
        {
            log.LogWarning("CMD_AUTH_GAMEID from {Session} is missing gameid or gameid_password", ctx.Session);
            return [ctx.Fail("ERR_AUTHGID_AUTH")];
        }

        if (!string.Equals(passwordType, "PASS_PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning(
                "CMD_AUTH_GAMEID from {Session} used unsupported password_type {Type}",
                ctx.Session, passwordType);
            return [ctx.Fail("ERR_AUTHGID_AUTH")];
        }

        var account = await accounts.GetByGameIdAsync(gameId, ctx.CancellationToken);
        if (account is null)
        {
            log.LogWarning("CMD_AUTH_GAMEID for unknown game id {GameId}", gameId);
            return [ctx.Fail("ERR_AUTHGID_AUTH")];
        }

        if (account.Banned)
        {
            log.LogWarning("Banned account {GameId} tried CMD_AUTH_GAMEID", account.GameId);
            return [ctx.Fail("ERR_AUTHGID_AUTH")];
        }

        var presented = AuthProof.HashPassword(password);
        if (!AuthProof.TryNormalizeMd5(account.PasswordHash, out var registered)
            || !AuthProof.FixedTimeEquals(registered, presented))
        {
            // No detail beyond this: the account exists and the password is wrong.
            log.LogWarning("CMD_AUTH_GAMEID failed the password check for {GameId}", account.GameId);
            return [ctx.Fail("ERR_AUTHGID_AUTH")];
        }

        var (loginId, loginPassword) = tickets.Mint(account.Id, account.GameId);

        log.LogInformation("CMD_AUTH_GAMEID issued a login ticket for account {GameId}", account.GameId);

        return
        [
            ctx.Ok()
                .Set("loginid", loginId)
                .Set("loginid_password", loginPassword),
        ];
    }

    /// <summary>
    /// Stage two of the login: <c>CMD_LOGIN</c> replays the pair minted by
    /// <c>CMD_AUTH_GAMEID</c> (<c>FUN_0061c360</c> state 7, sent from <c>FUN_006425c0</c>).
    /// On success the session becomes <see cref="SessionState.Authenticated"/> and the client
    /// moves on to <c>CMD_GET_PLAYERLIST</c> and <c>CMD_SET_LANGUAGE</c>, which the shared
    /// handlers already serve.
    /// </summary>
    /// <remarks>
    /// Only <c>account_first_login</c> is returned, because it is the only field of this
    /// response the binary is known to read back (<c>FUN_0061c360</c> state 8, literal at
    /// 0x0130c9b8). <c>accountid</c> does not exist as a literal anywhere in pes2011.exe, so it
    /// is deliberately not invented here.
    /// </remarks>
    [Command("CMD_LOGIN", Roles = LoginRoles, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.Connected)]
    public async ValueTask<KvMessage[]> Login(CommandContext ctx)
    {
        var style = ctx.Request.GetString("style");
        if (style is not null && !string.Equals(style, "LOGINID", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("CMD_LOGIN from {Session} used unsupported style {Style}", ctx.Session, style);
            return [ctx.Fail("ERR_LOGIN_FAILED")];
        }

        var ticket = tickets.Redeem(
            ctx.Request.GetString("loginid"),
            ctx.Request.GetString("loginid_password"));

        if (ticket is null)
        {
            log.LogWarning(
                "CMD_LOGIN from {Session} presented a login id the server did not mint or has expired",
                ctx.Session);
            return [ctx.Fail("ERR_LOGIN_FAILED")];
        }

        var account = await accounts.GetByGameIdAsync(ticket.GameId, ctx.CancellationToken);
        if (account is null || account.Id != ticket.AccountId)
        {
            log.LogWarning("CMD_LOGIN ticket named account {AccountId} which no longer exists", ticket.AccountId);
            return [ctx.Fail("ERR_INVALID_ACCOUNT")];
        }

        if (account.Banned)
        {
            log.LogWarning("Banned account {GameId} tried CMD_LOGIN", account.GameId);
            return [ctx.Fail("ERR_LOGIN_FAILED")];
        }

        // use_regcode/regcode are verified field names; when the client asks for the serial to
        // be checked the comparison is against the serial registered for the account, exactly
        // as the PES 2010 path does. use_regcode="NO" skips it.
        if (IsYes(ctx.Request.GetString("use_regcode")))
        {
            var presentedRegCode = ctx.Request.GetString("regcode")?.Trim().ToUpperInvariant();
            var registeredRegCode = account.RegCode?.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(registeredRegCode))
                return [ctx.Fail("ERR_REGCODENOTISSUED")];

            if (!string.Equals(registeredRegCode, presentedRegCode, StringComparison.Ordinal))
            {
                log.LogWarning(
                    "CMD_LOGIN for {GameId} presented a serial that does not match the registered one",
                    account.GameId);
                return [ctx.Fail("ERR_REGCODE_FAILED")];
            }
        }

        ctx.Session.AccountId = account.Id;
        ctx.Session.GameId = account.GameId;
        ctx.Session.RegCode = account.RegCode ?? "";
        ctx.Session.FirstLogin = await accounts.ClaimFirstLoginAsync(account.Id, ctx.CancellationToken);
        ctx.Session.State = SessionState.Authenticated;

        await accounts.TouchLoginAsync(account.Id, ctx.CancellationToken);

        log.LogInformation(
            "Account {GameId} logged in over PES 2011 CMD_LOGIN (first login: {FirstLogin})",
            account.GameId, ctx.Session.FirstLogin);

        return [ctx.Ok().Set("account_first_login", ctx.Session.FirstLogin)];
    }

    /// <summary>
    /// Protocol-wide booleans are the strings YES/NO (pes2011.exe enum type id 0x4c), but a
    /// leading 1 is accepted too so a client that sends the numeric form is not rejected.
    /// </summary>
    private static bool IsYes(string? value)
        => value is not null
           && (value.Equals("YES", StringComparison.OrdinalIgnoreCase) || value == "1");
}
