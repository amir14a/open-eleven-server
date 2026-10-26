// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>
/// The single global state store: every live session across every service port, plus
/// the rooms they occupy. Registered as a singleton and holds no scoped dependency,
/// which is why sessions are plain objects rather than DI-resolved services.
/// </summary>
public sealed class Hub(ILogger<Hub> log, IOptionsMonitor<ServerOptions> options, MatchResults results)
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<int, Room> _rooms = new();
    private int _nextRoomId;
    private int _nextPid = 1000;

    /// <summary>
    /// rqid used for a chat delivery to a session that has no parked CMD_WATCH_TEXTCHAT.
    /// The client arms its watch only when the screen changes, so most sessions have none
    /// for most of their life and delivery cannot be made to depend on it.
    /// </summary>
    public const int UnsolicitedTextChatRqid = 1;

    /// <summary>
    /// rqid for a room notice pushed to a session that did not ask for it.
    /// </summary>
    /// <remarks>
    /// A response's rqid selects which parked request on that connection it answers, so
    /// echoing the acting player's rqid onto a bystander's connection routes the push into
    /// whatever that bystander happens to have parked under the same number. Observed live:
    /// a joining player's rqid=11 reached the room owner, whose rqid 11 was its own
    /// MSG_REQRMEMBERLIST, and the owner's Lobby connection dropped. Pushes sent with 0
    /// were handled without incident.
    /// </remarks>
    public const int UnsolicitedRqid = 0;

    /// <summary>
    /// Notices whose names/fields are inferred rather than captured. Off by default:
    /// MSG_ROOMINNOTICE reproducibly closes the recipient's Lobby connection even with
    /// the payload FUN_00BB4D70 declares, so it must stay opt-in until that is solved.
    /// </summary>
    private bool EmitUnconfirmed => options.CurrentValue.Protocol.EmitUnconfirmedMessages;

    /// <summary>
    /// The endpoint <paramref name="viewer"/> should use to reach <paramref name="member"/>:
    /// the public (STUN) one, unless both sit behind the same NAT (same public IP). A router
    /// will not hairpin a packet back through its own public address, so peers on one LAN get
    /// each other's internal endpoint instead. Decided per pair, so a LAN pair and an internet
    /// pair can share one room.
    /// </summary>
    /// <remarks>
    /// PES 2010 gets the endpoints exactly as the member reported them. Its P2P handshake
    /// (FUN_00f8bd00) already probes in_ip when the peer's ex_ip equals its own, and the
    /// handshake id (FUN_00f8bc00) hashes the peer's ex_ip/ex_port against the peer's own
    /// view of them, so a rewritten endpoint makes every probe go unanswered.
    /// </remarks>
    public PeerEndpoints EndpointsFor(Session viewer, Session member)
    {
        if (!Is2011)
            return new(member.ExternalIp, member.ExternalPort, member.InternalIp, member.InternalPort);

        var internalUsable = member.InternalPort is > 0 and <= 65535
                             && !string.IsNullOrWhiteSpace(member.InternalIp);
        var externalUsable = member.ExternalPort is > 0 and <= 65535
                             && !string.IsNullOrWhiteSpace(member.ExternalIp)
                             && member.ExternalIp != "127.0.0.1";

        var sameNat = externalUsable && member.ExternalIp == viewer.ExternalIp;
        var useInternal = internalUsable && (!externalUsable || sameNat);
        if (useInternal)
            return new(member.InternalIp, member.InternalPort, member.InternalIp, member.InternalPort);

        return new(member.ExternalIp, member.ExternalPort, member.InternalIp, member.InternalPort);
    }

    public int SessionCount => _sessions.Count;

    public IReadOnlyCollection<Session> Sessions => _sessions.Values.ToArray();

    public void Register(Session session)
    {
        _sessions[session.Id] = session;
        log.LogDebug("Session registered: {Session} ({Count} live)", session, _sessions.Count);
    }

    public void Unregister(Session session)
    {
        _sessions.TryRemove(session.Id, out _);

        if (QuickMatch.Leave(session) is { } group)
            group.TellCancelled(session);

        if (session.RoomId is { } roomId)
            LeaveRoom(session, roomId);

        // Only once the player's last connection to this block is gone — a player holds
        // one connection per service, and the others still represent them in the block.
        if (session.Pid > 0 && !SessionsInBlock(session.BlockId ?? 0).Any(s =>
                s.Pid == session.Pid && s.Role == session.Role))
        {
            PublishBlockPlayerRemoved(session);
        }

        log.LogDebug("Session removed: {Session} ({Count} live)", session, _sessions.Count);
    }

    public int AllocatePid() => Interlocked.Increment(ref _nextPid);

    /// <summary>
    /// Copies the identity of an already-authenticated session on the same remote address
    /// onto a freshly opened one. With one listener per service the client holds several
    /// connections at once and does not repeat the login handshake on each, so without
    /// this every non-account port would see an anonymous session.
    /// </summary>
    public bool TryAdoptIdentity(Session session)
    {
        var donor = _sessions.Values
            .Where(s => s.Id != session.Id
                        && s.Pid > 0
                        && s.State >= SessionState.Authenticated
                        && s.Remote.Address.Equals(session.Remote.Address))
            .MaxBy(s => s.ConnectedAt);

        if (donor is null)
            return false;

        session.AccountId = donor.AccountId;
        session.GameId = donor.GameId;
        session.AuthHash = donor.AuthHash;
        session.ParaHash = donor.ParaHash;
        session.EntryHash = donor.EntryHash;
        session.RegCode = donor.RegCode;
        session.FirstLogin = donor.FirstLogin;
        session.Pid = donor.Pid;
        session.PlayerName = donor.PlayerName;
        session.DesiredPositionMask = donor.DesiredPositionMask;
        session.EulaAccepted = donor.EulaAccepted;
        session.ChatEnabled = donor.ChatEnabled;

        // Carry the login progression but never the room or match membership: those
        // belong to the connection that established them.
        session.State = donor.State < SessionState.PlayerSelected
            ? donor.State
            : SessionState.PlayerSelected;

        log.LogInformation(
            "Session {Session} adopted identity pid={Pid} '{Name}' from {Donor}",
            session.Id, session.Pid, session.PlayerName, donor.Role);

        return true;
    }

    /// <summary>Number of distinct selected players on a service, for the server list.</summary>
    public int PlayerCount(ServiceRole role)
        => _sessions.Values
            .Where(s => s.Role == role && s.Pid > 0 && s.State >= SessionState.PlayerSelected)
            .Select(s => s.Pid)
            .Distinct()
            .Count();

    public int ConnectedPlayerCount()
        => _sessions.Values
            .Where(s => s.Pid > 0 && s.State >= SessionState.PlayerSelected)
            .Select(s => s.Pid)
            .Distinct()
            .Count();

    /// <summary>
    /// All sessions belonging to one player. A player holds several connections at once
    /// because each service now lives on its own port.
    /// </summary>
    public IReadOnlyList<Session> SessionsForPid(int pid)
        => _sessions.Values.Where(s => s.Pid == pid && pid != 0).ToArray();

    public IReadOnlyList<Session> SessionsForAccount(int accountId)
        => _sessions.Values.Where(s => s.AccountId == accountId && accountId != 0).ToArray();

    /// <summary>
    /// Closes every connection of an account (an admin kick or ban); returns how many. Completing
    /// the outbound queue ends the writer, which closes the socket, so the client is sent nothing
    /// it has never been seen to receive.
    /// </summary>
    public int Disconnect(int accountId)
    {
        var sessions = SessionsForAccount(accountId);
        foreach (var session in sessions)
            session.CompleteOutbound();
        return sessions.Count;
    }

    /// <summary>Clears a deleted profile from every inactive service connection.</summary>
    public void ResetPlayerIdentity(int accountId, int pid)
    {
        foreach (var session in SessionsForAccount(accountId).Where(s => s.Pid == pid))
        {
            session.Pid = 0;
            session.PlayerName = "";
            session.DesiredPositionMask = 0;
            session.ChatEnabled = true;
            session.TextChatWatch = null;
            if (session.State >= SessionState.PlayerSelected)
                session.State = SessionState.Authenticated;
        }
    }

    public void SetPlayerChatEnabled(int accountId, int pid, bool enabled)
    {
        foreach (var session in SessionsForAccount(accountId).Where(s => s.Pid == pid))
        {
            session.ChatEnabled = enabled;
            if (!enabled)
                session.TextChatWatch = null;
        }
    }

    public IReadOnlyList<Session> SessionsInBlock(int blockId)
        => _sessions.Values.Where(s => s.BlockId == blockId).ToArray();

    /// <summary>Distinct players in any block: one player holds a session per service.</summary>
    public int PlayersInBlocks
        => _sessions.Values.Where(s => s.BlockId is not null && s.Pid > 0).Select(s => s.Pid).Distinct().Count();

    // ---- rooms ------------------------------------------------------------

    public IReadOnlyList<Room> Rooms => _rooms.Values.ToArray();

    public IReadOnlyList<Room> RoomsFor(Session session)
        => _rooms.Values
            .Where(room => room.ServiceRole == session.Role && room.BlockId == session.BlockId)
            .OrderBy(room => room.Id)
            .ToArray();

    public Room? FindRoom(int id) => _rooms.GetValueOrDefault(id);

    public Room RequireRoom(int id)
        => FindRoom(id) ?? throw new InvalidOperationException($"Room {id} does not exist.");

    /// <summary>Players searching for a quick match and the groups formed from them.</summary>
    public QuickMatch QuickMatch { get; } = new();

    /// <summary>
    /// The room a formed quick match plays in, so the shared room tail (CMD_SET_GAMEMEMBERENV,
    /// CMD_CHANGE_GAMEPHASE, results) works unchanged. It belongs to no block (id -1), so no
    /// room list shows it. Sides follow matching order: the owner is HOME.
    /// </summary>
    public Room CreateQuickMatchRoom(IReadOnlyList<Session> members, string matchType, KvMessage settings)
    {
        var owner = members[0];
        // One name for everyone in the room: the owner's language.
        var room = new Room(Interlocked.Increment(ref _nextRoomId), ServerText.Get("room.quickmatch", owner.Language), owner.Pid,
            members.Count, owner.Role, blockId: -1)
        {
            MatchType = matchType,
            GameEnvironment = settings,
        };
        _rooms[room.Id] = room;

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (member.RoomId is { } previous)
                LeaveRoom(member, previous);
            room.TryJoin(member);
            member.RoomId = room.Id;
            member.State = SessionState.InRoom;
            member.GameSide = i % 2;
        }

        log.LogInformation("Room {RoomId} created for quick match ({Type}): pids {Pids}",
            room.Id, matchType, string.Join(", ", members.Select(m => m.Pid)));
        return room;
    }

    public Room CreateRoom(Session owner, string name, int maxMembers)
    {
        if (owner.BlockId is not { } blockId)
            throw new InvalidOperationException("A session must select a block before creating a room.");

        var room = new Room(
            Interlocked.Increment(ref _nextRoomId),
            name,
            owner.Pid,
            maxMembers,
            owner.Role,
            blockId);
        _rooms[room.Id] = room;
        var joined = room.TryJoin(owner);
        if (joined.Status != RoomJoinStatus.Joined)
            throw new InvalidOperationException("The room owner could not join the newly created room.");

        owner.RoomId = room.Id;
        owner.State = SessionState.InRoom;

        log.LogInformation("Room {RoomId} '{Name}' created by pid {Pid}", room.Id, name, owner.Pid);
        return room;
    }

    public RoomJoinResult JoinRoom(Session session, int roomId, string password)
    {
        if (session.RoomId is not null)
            return new RoomJoinResult(RoomJoinStatus.AlreadyInRoom, null, []);

        var room = FindRoom(roomId);
        if (room is null
            || room.ServiceRole != session.Role
            || room.BlockId != session.BlockId)
            return new RoomJoinResult(RoomJoinStatus.RoomNotFound, room, []);

        if (!string.Equals(room.Password, password, StringComparison.Ordinal))
            return new RoomJoinResult(RoomJoinStatus.WrongPassword, room, []);

        var result = room.TryJoin(session);
        if (result.Status != RoomJoinStatus.Joined)
            return result;

        session.RoomId = room.Id;
        session.State = SessionState.InRoom;
        return result;
    }

    public RoomLeaveResult LeaveRoom(Session session, int roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
        {
            session.RoomId = null;
            if (session.State >= SessionState.InRoom)
                session.State = SessionState.InBlock;
            return new RoomLeaveResult(RoomLeaveStatus.RoomNotFound, roomId, false, false);
        }

        // Leaving a match in progress (connection dropped or CMD_LEAVE_ROOM), or being the
        // player the other one just forfeited over, is a disconnect. Read before removal.
        var lineup = MatchResults.Lineup(room);
        var walkedOut = Is2011 && (room.Status == "GAME"
                                   || room.ForfeitStayerPid is { } stayer && stayer != session.Pid);

        var removal = room.Remove(session);
        if (!removal.Removed)
            return new RoomLeaveResult(RoomLeaveStatus.NotInRoom, roomId, false, false);

        if (walkedOut)
            _ = results.RecordAsync(room, lineup, leaverPid: session.Pid, noMatch: false);

        session.RoomId = null;
        session.GameEntryWatchRqid = null;
        session.RoomStateWatchRqid = null;
        session.RoomPlayerWatch = null;
        if (session.State >= SessionState.InRoom)
            session.State = SessionState.InBlock;

        if (room.IsEmpty)
        {
            _rooms.TryRemove(roomId, out _);
            log.LogInformation("Room {RoomId} disposed (empty)", roomId);
            PublishRoomDeleted(room, session.Id);
            return new RoomLeaveResult(RoomLeaveStatus.Left, roomId, true, removal.OwnerChanged);
        }

        // PES 2010 drops the player from its room player list through FUN_00BB4D70; the
        // MSG_REQRMEMBERLIST task there takes only one roster stream, so the refresh below
        // does not reach it. Other titles keep the notice behind EmitUnconfirmedMessages.
        if (EmitUnconfirmed || Is2010)
        {
            var notice = KvMessage.Ok("MSG_PxROOMOUTNOTICE", UnsolicitedRqid).Set("pid", session.Pid);
            foreach (var remaining in removal.RemainingMembers)
                remaining.Push(notice);
        }

        if (Is2011)
            PublishRoomPlayerLeft(room, session.Pid);
        PublishRoomRoster(room);

        // The entry-game screen tracks departures through its own pair of watches: who left,
        // then the side assignments that leaving may have reshuffled.
        PublishDisconPlayerEnv(room, session.Pid);
        PublishDisconPlayerMatch(room);

        if (removal.OwnerChanged)
            PublishRoomStateChanged(room, "OwnerChanged");

        // A match cannot go on alone: WAITING is what makes the remaining PES 2011 client
        // leave its in-match screen (FUN_005fa030) instead of waiting forever.
        if (Is2011 && room.Count < 2)
            SetRoomStatus(room, "WAITING");

        PublishRoomUpdated(room, session.Id);
        return new RoomLeaveResult(RoomLeaveStatus.Left, roomId, false, removal.OwnerChanged);
    }

    public void PublishRoomJoined(RoomJoinResult join, Session member)
    {
        if (join is not { Status: RoomJoinStatus.Joined, Room: { } room })
            return;

        // FUN_00BB4D70 consumes these exact fields for MSG_ROOMINNOTICE. In PES 2010 it is
        // the only way an occupant learns of the newcomer (its room player list comes from a
        // one-shot MSG_REQRMEMBERLIST task, FUN_00BB5190); without it the host never adds
        // the joiner as a P2P peer and drops its probes. Opt-in for the other titles.
        if (EmitUnconfirmed || Is2010)
        {
            foreach (var existing in join.ExistingMembers)
            {
                var endpoints = EndpointsFor(existing, member);
                var notice = KvMessage.Ok("MSG_ROOMINNOTICE", UnsolicitedRqid)
                    .Set("ex_ip", endpoints.ExternalIp)
                    .Set("ex_port", endpoints.ExternalPort)
                    .Set("in_ip", endpoints.InternalIp)
                    .Set("in_port", endpoints.InternalPort)
                    .Set("pid", member.Pid)
                    // The joining member's room-local id, not the host's.
                    .Set("room_pid", RoomPresenter.RoomPlayerNumber(member))
                    .Set("room_entry_no", member.RoomEntryNo)
                    .Set("game_entry_no", -1);
                existing.Push(notice);
            }
        }

        // The occupants already cached their roster; a fresh START/LIST/END is what
        // makes the joining player appear on their screen. The joiner is skipped: its
        // CMD_JOIN_ROOM response has not been queued yet, so anything pushed first would
        // arrive ahead of the response its state machine is waiting for.
        foreach (var existing in join.ExistingMembers)
            PublishPeerEndpoints(room, existing);
        PublishRoomRoster(room, except: member.Id);

        // WAITING is the one-player room-browser state. Once another player joins,
        // the configured room enters the pre-match SETENV state. Do not advance an
        // unconfigured room or regress a room already beyond pre-match.
        // Not for PES 2010: its CMD_ENTRY_GAME task (FUN_007b7bc0) only sends while the
        // room is WAITING, and without those entries "Start Match" stays disabled. That
        // client moves the room to SETENV itself with CMD_UPDATE_ROOMSTATE.
        if (!Is2010 && room.Count > 1 && room.GameEnvironment is not null && room.Status == "WAITING")
            room.TrySetStatus("SETENV");

        PublishRoomStateChanged(room, "StateChanged", member.Id);

        PublishRoomUpdated(room, member.Id);
    }

    public int PublishPeerEndpoints(Room room, Session? onlyRecipient = null)
    {
        var members = room.Snapshot();
        var sent = 0;
        foreach (var recipient in members)
        {
            if (onlyRecipient is not null && recipient.Id != onlyRecipient.Id)
                continue;
            // PES2010 registers this as a local CMD_COMMON_WATCH during Lobby
            // initialization; it does not send a matching request to the server.
            // Other clients may still park a request and provide an rqid.
            var rqid = recipient.IpAndPortWatchRqid ?? UnsolicitedRqid;

            foreach (var peer in members.Where(peer => peer.Pid != recipient.Pid))
            {
                var endpoints = EndpointsFor(recipient, peer);
                var update = KvMessage.Ok("CMD_WATCH_IPANDPORT", rqid)
                    .Set("ex_ip", endpoints.ExternalIp)
                    .Set("ex_port", endpoints.ExternalPort)
                    .Set("in_ip", endpoints.InternalIp)
                    .Set("in_port", endpoints.InternalPort)
                    .Set("pid", peer.Pid);
                if (recipient.Push(update))
                    sent++;
            }
        }

        return sent;
    }

    /// <summary>
    /// Pushes a complete member roster using the same START/LIST/END stream the client
    /// requests with MSG_REQRMEMBERLIST. FUN_00BB5190 rebuilds the roster from it, which
    /// is what lets an occupant see somebody who joined or left after the room screen
    /// was first drawn.
    /// </summary>
    /// <remarks>
    /// Unsolicited: this is not a reply to a parked request, so it carries rqid 0. A
    /// response's rqid selects which request it answers on that connection, and echoing
    /// another player's rqid here would deliver the roster into the wrong state machine.
    /// </remarks>
    public int PublishRoomRoster(Room room, Guid? except = null, int rqid = UnsolicitedRqid)
    {
        if (Is2011)
            PublishRoomPlayers(room);

        var members = room.Snapshot();
        if (members.Count == 0)
            return 0;

        var sequence = RoomPresenter.MemberListStream(room, rqid);
        if (!Is2011)
            sequence = [.. sequence, RoomPresenter.RoomPlayerWatch(room, UnsolicitedRqid)];

        var sent = 0;
        foreach (var member in members)
        {
            if (member.Id == except)
                continue;

            foreach (var message in sequence)
                if (member.Push(message))
                    sent++;
        }

        return sent;
    }

    /// <summary>room_id's members, each with the endpoints <paramref name="viewer"/> must use.</summary>
    public IReadOnlyList<KvMessage> RoomPlayerEntries(Room? room, Session viewer)
        => (room?.Snapshot() ?? []).Take(8)
            .Select(m => Pes2011Lists.RoomPlayerEntry(m, EndpointsFor(viewer, m))).ToArray();

    /// <summary>Re-sends the whole roster (upsert) to every member watching it.</summary>
    public void PublishRoomPlayers(Room room)
    {
        foreach (var watcher in room.Snapshot().Where(s => s.RoomPlayerWatch is not null))
        {
            var sub = watcher.RoomPlayerWatch!;
            watcher.Push(KvMessage.Ok(sub.Msg, sub.Rqid)
                .Set("list_type", "ADD")
                .SetList(sub.NumName, sub.ListName, RoomPlayerEntries(room, watcher)));
        }
    }

    /// <summary>Tells the remaining members that <paramref name="pid"/> is gone.</summary>
    private void PublishRoomPlayerLeft(Room room, int pid)
    {
        foreach (var watcher in room.Snapshot().Where(s => s.RoomPlayerWatch is not null))
        {
            var sub = watcher.RoomPlayerWatch!;
            watcher.Push(KvMessage.Ok(sub.Msg, sub.Rqid)
                .Set("list_type", "DEL")
                .SetList(sub.NumName, sub.ListName, [new KvMessage().Set("pid", pid)]));
        }
    }

    private bool Is2011 => options.CurrentValue.GameProfile == GameProfile.Pes2011Pc;

    private bool Is2010 => options.CurrentValue.GameProfile == GameProfile.Pes2010Pc;

    // The titles served by the in-match handlers in Pes2011Handlers (see MatchProfiles there).
    private bool HasMatchFlow => options.CurrentValue.GameProfile is GameProfile.Pes2010Pc or GameProfile.Pes2011Pc;

    /// <summary>
    /// PES 2011 list watches (CMD_REQWATCHL_*): an ADD/UPDATE/DEL delta pushed under the
    /// msg and rqid the client registered with, to everyone in the same block, including
    /// players sitting inside a room: they must come back to an up-to-date list.
    /// </summary>
    private int PushWatch(Func<Session, WatchListSubscription?> pick, ServiceRole role, int? blockId,
        string listType, KvMessage entry, Guid? except)
    {
        var sent = 0;
        foreach (var s in _sessions.Values.Where(s =>
                     s.Role == role && s.BlockId == blockId && s.Id != except))
        {
            if (pick(s) is { } sub && s.Push(WatchDelta(sub, listType, entry)))
                sent++;
        }

        return sent;
    }

    private static KvMessage WatchDelta(WatchListSubscription sub, string listType, KvMessage entry)
        => KvMessage.Ok(sub.Msg, sub.Rqid)
            .Set("list_type", listType)
            .SetList(sub.NumName, sub.ListName, [entry]);

    /// <summary>
    /// The room-list UPDATE for a player who just joined <paramref name="room"/>. Its room screen
    /// draws the members from its cached room_list entry (gamer[]), so without this it only shows
    /// the players that were there before it.
    /// </summary>
    public KvMessage? RoomListUpdateFor(Session viewer, Room room)
        => Is2011 && viewer.RoomListWatch is { } sub ? WatchDelta(sub, "UPDATE", Pes2011Lists.RoomEntry(room)) : null;

    public void PublishRoomUpdated(Room room, Guid? except = null)
    {
        if (Is2011)
        {
            PushWatch(s => s.RoomListWatch, room.ServiceRole, room.BlockId, "UPDATE", Pes2011Lists.RoomEntry(room), except);
            return;
        }

        BroadcastRoomListToBlock(room, RoomListUpdate(room), except);
    }

    // FUN_00BB9B10 feeds updates through FUN_00BB9200, so updates need the same
    // complete entry as the initial list rather than an ID-only placeholder.
    public KvMessage RoomListUpdate(Room room)
        => KvMessage.Ok("MSG_ROOMLISTUPDATE", UnsolicitedRqid)
            .SetList("count", "roomList", [RoomPresenter.ListEntry(room)]);

    public int PublishGameEntryChanged(Room room)
    {
        // PES 2011 reads each player's game_entry_no from its roomplayer watch, not from this.
        if (Is2011)
            PublishRoomPlayers(room);

        var members = room.Snapshot();
        // PES 2011 (FUN_005e4080) starts the match once every listed entry has a gameNum and
        // there are at least two, so it gets the players in the room, not the room's slots:
        // empty slots would keep a half-full room waiting forever.
        // PES 2010 (FUN_0072AEB0) iterates entry_count records as fixed room slots and
        // requires every unoccupied slot to be present with gameNum=-1; sending only
        // occupied members leaves that client with an incomplete snapshot.
        var slots = Is2011
            ? members.Select(m => m.RoomEntryNo).Order()
            : Enumerable.Range(0, room.MaxMembers);
        var entries = slots
            .Select(slot =>
            {
                var member = members.FirstOrDefault(candidate => candidate.RoomEntryNo == slot);
                return new KvMessage()
                    .Set("pid", member?.Pid ?? -1)
                    .Set("entryNum", slot)
                    .Set("gameNum", member?.GameEntryNo ?? -1);
            })
            .ToArray();

        var sent = 0;
        foreach (var member in members)
        {
            // PES2010 arms this watch locally (FUN_007bacc0) and does not send a
            // matching wire request, so it gets rqid 0; a parked rqid is still used if sent.
            var rqid = member.GameEntryWatchRqid ?? UnsolicitedRqid;

            var update = KvMessage.Ok("CMD_WATCH_ENTRY_GAME", rqid)
                .SetList("entry_count", "entrygame", entries);
            if (member.Push(update))
                sent++;
        }

        return sent;
    }

    /// <summary>
    /// Rebroadcasts the host's match settings. FUN_0072E630 reads the same game_env record
    /// CMD_SET_GAMEENV carries, so a member that never sees this never learns the settings.
    /// </summary>
    public int PublishDecideGameEnv(Room room)
    {
        if (room.GameEnvironment is null)
            return 0;

        var sent = 0;
        foreach (var member in room.Snapshot())
        {
            var rqid = member.DecideGameEnvWatchRqid ?? UnsolicitedRqid;
            var update = KvMessage.Ok("CMD_WATCH_DECIDE_GAMEENV", rqid)
                .Set("game_env", room.GameEnvironment);
            if (member.Push(update))
                sent++;
        }

        return sent;
    }

    /// <summary>
    /// The room roster as fixed slots. FUN_0072ED20 reads status and player_count, then
    /// walks pid and video as plain scalar lists of one entry per slot — the same bracketed
    /// form as desiredPosition, not brace-wrapped records.
    /// </summary>
    public int PublishDecideGamePlayer(Room room)
    {
        var members = room.Snapshot();

        // FUN_0072ED20 walks a fixed eight slots, whatever the room size is: it reads the
        // scalar key "pid" eight times and "video" eight times, not a bracketed list. A
        // bracketed value makes its integer parser fall back to 0x7b, so send eight
        // scalars and fill unused slots with -1.
        const int rosterSlots = 8;
        var pids = Enumerable.Range(0, rosterSlots)
            .Select(slot => (object?)(members.FirstOrDefault(m => m.RoomEntryNo == slot)?.Pid ?? -1))
            .ToArray();

        var sent = 0;
        foreach (var member in members)
        {
            var rqid = member.DecideGamePlayerWatchRqid ?? UnsolicitedRqid;
            var update = KvMessage.Ok("CMD_WATCH_DECIDE_GAMEPLAYER", rqid)
                .Set("status", room.Status)
                .Set("player_count", members.Count);

            foreach (var pid in pids)
                update.Set("pid", pid);

            // Nothing captures a client's refresh rate yet: CMD_SET_CURRENTPLAYER carries
            // frame_rate but the account handler does not read it. 60Hz for every slot
            // until that is wired through.
            for (var slot = 0; slot < rosterSlots; slot++)
                update.Set("video", "FRAME_60");

            if (member.Push(update))
                sent++;
        }

        return sent;
    }

    /// <summary>
    /// Side and captain assignment. FUN_0072F020 reads count plus sideinfo[i].{pid,side,
    /// sideLeader}; FUN_0072F300 reads the identical records under sideInfo — the capital I
    /// is the client's own spelling and the two are not interchangeable.
    /// </summary>
    public int PublishDecideGamePlayerEnv(Room room)
        => PublishSideInfo(
            room,
            "CMD_WATCH_DECIDE_GAMEPLAYERENV",
            "sideinfo",
            member => member.DecideGamePlayerEnvWatchRqid);

    public int PublishDisconPlayerMatch(Room room)
        => PublishSideInfo(
            room,
            "CMD_WATCH_DISCON_PLAYERMATCH",
            "sideInfo",
            member => member.DisconPlayerMatchWatchRqid);

    private int PublishSideInfo(
        Room room,
        string msg,
        string listKey,
        Func<Session, int?> watchRqid)
    {
        var entries = BuildSideInfoEntries(room);
        var sent = 0;
        foreach (var member in room.Snapshot())
        {
            var update = KvMessage.Ok(msg, watchRqid(member) ?? UnsolicitedRqid)
                .SetList("count", listKey, entries);
            if (member.Push(update))
                sent++;
        }

        return sent;
    }

    /// <summary>
    /// One record per member that has taken a side. The room owner leads HOME; the lowest
    /// entry number on AWAY leads that side.
    /// </summary>
    private static KvMessage[] BuildSideInfoEntries(Room room)
    {
        var sided = room.Snapshot().Where(member => member.GameSide is 0 or 1).ToArray();
        var awayLeaderId = sided
            .Where(member => member.GameSide == 1)
            .OrderBy(member => member.RoomEntryNo)
            .FirstOrDefault()?.Id;

        return sided.Select(member => new KvMessage()
                .Set("pid", member.Pid)
                // Enum type 0x19 accepts only HOME/AWAY, type 0x36 only NO/YES.
                .Set("side", member.GameSide == 0 ? "HOME" : "AWAY")
                .Set("sideLeader",
                    (member.GameSide == 0 && member.Pid == room.OwnerPid) || member.Id == awayLeaderId
                        ? "YES"
                        : "NO"))
            .ToArray();
    }

    /// <summary>Who left. FUN_0072AD70 reads a single top-level pid.</summary>
    public int PublishDisconPlayerEnv(Room room, int leftPid)
    {
        var sent = 0;
        foreach (var member in room.Snapshot())
        {
            var rqid = member.DisconPlayerEnvWatchRqid ?? UnsolicitedRqid;
            if (member.Push(KvMessage.Ok("CMD_WATCH_DISCON_PLAYERENV", rqid).Set("pid", leftPid)))
                sent++;
        }

        return sent;
    }

    public int PublishRoomStateChanged(Room room, string eventName = "StateChanged")
    {
        var sent = 0;
        foreach (var member in room.Snapshot())
        {
            if (BuildRoomStateUpdate(room, member, eventName) is { } update && member.Push(update))
                sent++;
        }

        return sent;
    }

    public int PublishRoomStateChanged(Room room, string eventName, Guid except)
    {
        var sent = 0;
        foreach (var member in room.Snapshot().Where(member => member.Id != except))
        {
            if (BuildRoomStateUpdate(room, member, eventName) is { } update && member.Push(update))
                sent++;
        }

        return sent;
    }

    /// <summary>Moves the room to <paramref name="status"/> and tells its members; false when unchanged.</summary>
    public bool SetRoomStatus(Room room, string status)
    {
        if (room.Status == status || !room.TrySetStatus(status))
            return false;

        log.LogInformation("Room {RoomId} is now {Status}", room.Id, status);
        PublishRoomStateChanged(room);
        return true;
    }

    public KvMessage? BuildRoomStateUpdate(Room room, Session member, string eventName = "StateChanged")
        => KvMessage.Ok("CMD_WATCH_ROOMSTATE", member.RoomStateWatchRqid ?? UnsolicitedRqid)
            .Set("event", eventName)
            .Set("owner_pid", room.OwnerPid)
            .Set("state", room.Status);

    private void PublishRoomDeleted(Room room, Guid? except)
    {
        if (Is2011)
        {
            PushWatch(s => s.RoomListWatch, room.ServiceRole, room.BlockId, "DEL", new KvMessage().Set("room_id", room.Id), null);
            return;
        }

        // FUN_00BB9C70 requires rid for MSG_ROOMLISTDEL.
        var deleted = KvMessage.Ok("MSG_ROOMLISTDEL", UnsolicitedRqid).Set("rid", room.Id);
        BroadcastRoomListToBlock(room, deleted, except);
    }

    /// <summary>
    /// Announces a player to everyone already in the block, so their cached roster gains
    /// the entry that MSG_REQBLOCKPLAYERLIST would have given a later arrival.
    /// </summary>
    /// <remarks>
    /// FUN_0106B920 handles MSG_BLOCKPLAYERLISTADD and MSG_BLOCKPLAYERLISTUPDATE through
    /// one path: it reads the record under "block_player" and merges it via FUN_0106B2B0.
    /// Without this, a client that requested the roster while alone never learns about
    /// anyone who arrives afterwards.
    /// </remarks>
    public int PublishBlockPlayerAdded(Session member, KvMessage entry)
        => Is2011
            ? PushWatch(s => s.BlockPlayerWatch, member.Role, member.BlockId, "ADD", Pes2011Lists.BlockPlayerEntry(entry), member.Id)
            : BroadcastToBlock(
            member,
            KvMessage.Ok("MSG_BLOCKPLAYERLISTADD", UnsolicitedRqid).Set("block_player", entry));

    /// <summary>Removes a player from every other cached roster in the block.</summary>
    /// <remarks>FUN_0106B920 reads a single "pid" for MSG_BLOCKPLAYERLISTDEL.</remarks>
    public int PublishBlockPlayerRemoved(Session member)
        => member.Pid <= 0
            ? 0
            : Is2011
            ? PushWatch(s => s.BlockPlayerWatch, member.Role, member.BlockId, "DEL", new KvMessage().Set("pid", member.Pid), member.Id)
            : BroadcastToBlock(
                member,
                KvMessage.Ok("MSG_BLOCKPLAYERLISTDEL", UnsolicitedRqid).Set("pid", member.Pid));

    private int BroadcastToBlock(Session member, KvMessage message)
    {
        if (member.BlockId is not { } blockId)
            return 0;

        var sent = 0;
        foreach (var peer in _sessions.Values.Where(session =>
                     session.BlockId == blockId
                     && session.Role == member.Role
                     && session.Pid > 0
                     && session.Pid != member.Pid))
        {
            if (peer.Push(message))
                sent++;
        }

        return sent;
    }

    private int BroadcastRoomListToBlock(Room room, KvMessage message, Guid? except)
    {
        var sent = 0;
        // Room-list changes are block-scoped. The Lobby client keeps the browser cache
        // for the whole block and may not issue MSG_REQROOMLIST again after connecting,
        // so delivery cannot depend on a per-request subscription flag. PES 2010 keeps its
        // room-list watch running inside a room (FUN_007c0bd0) and draws the room screen from
        // that cache, so its InRoom sessions stay subscribed; other titles exclude them.
        foreach (var subscriber in _sessions.Values.Where(session =>
                     (session.State == SessionState.InBlock || Is2010 && session.State == SessionState.InRoom)
                     && session.Role == room.ServiceRole
                     && session.BlockId == room.BlockId))
        {
            if (subscriber.Id == except)
                continue;
            if (subscriber.Push(message))
                sent++;
        }

        return sent;
    }

    // ---- pushes -----------------------------------------------------------

    /// <summary>Server-initiated push to one room. This is what a request/response-only
    /// design cannot do, and what room, chat and match-offer flows are built on.</summary>
    public int BroadcastToRoom(int roomId, KvMessage message, Guid? except = null)
    {
        var room = FindRoom(roomId);
        if (room is null) return 0;

        var sent = 0;
        foreach (var member in room.Snapshot())
        {
            if (member.Id == except) continue;
            if (member.Push(message)) sent++;
        }

        return sent;
    }

    public int BroadcastToBlock(int blockId, KvMessage message, Guid? except = null)
    {
        var sent = 0;
        foreach (var session in SessionsInBlock(blockId))
        {
            if (session.Id == except) continue;
            if (session.Push(message)) sent++;
        }

        return sent;
    }

    /// <summary>
    /// Broadcasts one chat event to every player in the sender's area.
    /// FUN_00739D10 consumes these exact fields from CMD_WATCH_TEXTCHAT responses.
    /// </summary>
    /// <remarks>
    /// Chat is a broadcast, but a contained one: BLOCK reaches everyone in the sender's
    /// block and ROOM everyone in the sender's room, and neither reaches past that.
    /// <para>
    /// A parked watch is answered under its own rqid when one exists, but its absence does
    /// not suppress delivery. The client arms a watch only on a screen transition, so
    /// requiring one would silently discard everything said in between; sessions without
    /// one get <see cref="UnsolicitedTextChatRqid"/>.
    /// </para>
    /// </remarks>
    public int PublishTextChat(
        Session sender,
        TextChatChannel channel,
        string statement,
        IReadOnlySet<int> excludedPids,
        Guid? except = null)
    {
        var (scope, pidsInScope) = ResolveChatScope(sender, channel);

        var sent = 0;
        var unsolicited = 0;

        foreach (var pid in pidsInScope)
        {
            if (pid <= 0 || (pid != sender.Pid && excludedPids.Contains(pid)))
                continue;

            // One player holds a connection per service but has a single chat window, so
            // pick exactly one session to write to. Prefer the one with a parked watch —
            // that is the request the client is actually waiting on.
            var recipient = SessionsForPid(pid)
                .Where(s => s.Id != except && s.ChatEnabled)
                .OrderByDescending(s => s.TextChatWatch is not null)
                .ThenByDescending(s => s.Role == sender.Role)
                .FirstOrDefault();

            if (recipient is null)
                continue;

            var rqid = recipient.TextChatWatch?.Rqid ?? UnsolicitedTextChatRqid;
            if (recipient.TextChatWatch is null)
                unsolicited++;

            var delivery = KvMessage.Ok("CMD_WATCH_TEXTCHAT", rqid)
                .Set("from_pid", sender.Pid)
                .Set("channel", ToWire(channel))
                .Set("name", sender.PlayerName)
                .Set("statement", statement);

            if (recipient.Push(delivery))
                sent++;
            else
                log.LogWarning(
                    "Outbound queue full; dropped {Msg} for {Session}",
                    delivery.MsgName, recipient);
        }

        log.LogInformation(
            "Text chat from pid {Pid} ({Channel}) broadcast to {Sent} of {InScope} player(s) " +
            "in {Scope}, {Unsolicited} of them with no parked watch",
            sender.Pid, channel, sent, pidsInScope.Count, scope, unsolicited);

        return sent;
    }

    /// <summary>
    /// The players a chat message is contained to. BLOCK reaches the sender's block and
    /// ROOM the sender's room — never beyond either.
    /// </summary>
    /// <remarks>
    /// The area is looked up across all of the sender's connections, not just the one the
    /// message arrived on: rooms and blocks are joined per connection, so the socket
    /// carrying the chat is frequently not the socket that holds the membership.
    /// </remarks>
    private (string Scope, IReadOnlyList<int> Pids) ResolveChatScope(
        Session sender, TextChatChannel channel)
    {
        var senderSessions = SessionsForPid(sender.Pid);

        // Quick match chat (GAME_QUICK, TEAM_QUICK) happens in the match's room; the TEAM
        // channels reach only the sender's own side.
        if (channel is TextChatChannel.Room or TextChatChannel.GameQuick
            or TextChatChannel.Team or TextChatChannel.TeamQuick)
        {
            var inRoom = sender.RoomId is not null
                ? sender
                : senderSessions.FirstOrDefault(s => s.RoomId is not null);

            if (inRoom?.RoomId is { } id && FindRoom(id) is { } room)
            {
                var team = channel is TextChatChannel.Team or TextChatChannel.TeamQuick;
                return ($"room {id}", room.Snapshot()
                    .Where(s => !team || s.GameSide == inRoom.GameSide)
                    .Select(s => s.Pid).Distinct().ToArray());
            }

            log.LogWarning("Room chat from pid {Pid} but no room membership found", sender.Pid);
            return ("no room", []);
        }

        var blockId = sender.BlockId
                      ?? senderSessions.FirstOrDefault(s => s.BlockId is not null)?.BlockId;

        if (blockId is { } block)
            return ($"block {block}", SessionsInBlock(block).Select(s => s.Pid).Distinct().ToArray());

        log.LogWarning("Block chat from pid {Pid} but no block membership found", sender.Pid);
        return ("no block", []);
    }

    private static TextChatScene CurrentTextChatScene(Session session)
        => session.RoomId is null ? TextChatScene.Lobby : TextChatScene.Room;

    private static string ToWire(TextChatChannel channel)
        => channel switch
        {
            TextChatChannel.Block => "BLOCK",
            TextChatChannel.Room => "ROOM",
            TextChatChannel.Team => "TEAM",
            TextChatChannel.GameQuick => "GAME_QUICK",
            TextChatChannel.TeamQuick => "TEAM_QUICK",
            TextChatChannel.Competition => "COMPETITION",
            TextChatChannel.Community => "COMMUNITY",
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };

    /// <summary>Pushes to every connection a player currently holds, on any service port.</summary>
    public int PushToPlayer(int pid, KvMessage message)
    {
        var sent = 0;
        foreach (var session in SessionsForPid(pid))
            if (session.Push(message)) sent++;
        return sent;
    }
}

/// <summary>The two endpoint pairs one peer advertises to another.</summary>
public readonly record struct PeerEndpoints(
    string ExternalIp,
    int ExternalPort,
    string InternalIp,
    int InternalPort);
