// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>
/// The single global state store: every live session across every service port, plus
/// the rooms they occupy. Registered as a singleton and holds no scoped dependency,
/// which is why sessions are plain objects rather than DI-resolved services.
/// </summary>
public sealed class Hub(ILogger<Hub> log)
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

        var removal = room.Remove(session);
        if (!removal.Removed)
            return new RoomLeaveResult(RoomLeaveStatus.NotInRoom, roomId, false, false);

        session.RoomId = null;
        session.GameEntryWatchRqid = null;
        session.RoomStateWatchRqid = null;
        if (session.State >= SessionState.InRoom)
            session.State = SessionState.InBlock;

        if (room.IsEmpty)
        {
            _rooms.TryRemove(roomId, out _);
            log.LogInformation("Room {RoomId} disposed (empty)", roomId);
            PublishRoomDeleted(room, session.Id);
            return new RoomLeaveResult(RoomLeaveStatus.Left, roomId, true, removal.OwnerChanged);
        }

        var notice = KvMessage.Ok("MSG_PxROOMOUTNOTICE", UnsolicitedRqid).Set("pid", session.Pid);
        foreach (var remaining in removal.RemainingMembers)
            remaining.Push(notice);

        // The entry-game screen tracks departures through its own pair of watches: who left,
        // then the side assignments that leaving may have reshuffled.
        PublishDisconPlayerEnv(room, session.Pid);
        PublishDisconPlayerMatch(room);

        if (removal.OwnerChanged)
            PublishRoomStateChanged(room, "OwnerChanged");

        PublishRoomUpdated(room, session.Id);
        return new RoomLeaveResult(RoomLeaveStatus.Left, roomId, false, removal.OwnerChanged);
    }

    public void PublishRoomJoined(RoomJoinResult join, Session member)
    {
        if (join is not { Status: RoomJoinStatus.Joined, Room: { } room })
            return;

        // FUN_00BB4D70 consumes these exact fields for MSG_ROOMINNOTICE. Earlier traces
        // blamed this notice for the owner's disconnect, but a run with it suppressed
        // reproduced the failure: the actual bad delivery was MSG_ROOMLISTUPDATE being
        // sent to the owner after it had left the room-browser state.
        foreach (var existing in join.ExistingMembers)
        {
            var notice = KvMessage.Ok("MSG_ROOMINNOTICE", UnsolicitedRqid)
                .Set("ex_ip", member.ExternalIp)
                .Set("ex_port", member.ExternalPort)
                .Set("in_ip", member.InternalIp)
                .Set("in_port", member.InternalPort)
                .Set("pid", member.Pid)
                // room_pid identifies the room owner. The client keeps the joining
                // occupant's identity separately in pid.
                .Set("room_pid", room.OwnerPid)
                .Set("room_entry_no", member.RoomEntryNo)
                .Set("game_entry_no", -1);
            existing.Push(notice);
            PublishPeerEndpoints(room, existing);
        }

        // WAITING is the one-player room-browser state. Once another player joins,
        // the configured room enters the pre-match SETENV state. Do not advance an
        // unconfigured room or regress a room already beyond pre-match.
        if (room.Count > 1 && room.GameEnvironment is not null && room.Status == "WAITING")
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
                var update = KvMessage.Ok("CMD_WATCH_IPANDPORT", rqid)
                    .Set("ex_ip", peer.ExternalIp)
                    .Set("ex_port", peer.ExternalPort)
                    .Set("in_ip", peer.InternalIp)
                    .Set("in_port", peer.InternalPort)
                    .Set("pid", peer.Pid);
                if (recipient.Push(update))
                    sent++;
            }
        }

        return sent;
    }

    public void PublishRoomUpdated(Room room, Guid? except = null)
    {
        // FUN_00BB9B10 feeds updates through FUN_00BB9200, so updates need the same
        // complete entry as the initial list rather than an ID-only placeholder.
        var update = KvMessage.Ok("MSG_ROOMLISTUPDATE", UnsolicitedRqid)
            .SetList("count", "roomList", [RoomPresenter.ListEntry(room)]);
        BroadcastRoomListToBlock(room, update, except);
    }

    public int PublishGameEntryChanged(Room room)
    {
        var members = room.Snapshot();
        // FUN_0072AEB0 iterates entry_count records as fixed room slots. It also
        // requires every unoccupied slot to be present with gameNum=-1; sending
        // only occupied members leaves the client with an incomplete snapshot.
        var entries = Enumerable.Range(0, room.MaxMembers)
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
            // matching wire request.  A parked rqid is still supported for other
            // protocol variants, but the normal client needs unsolicited rqid=0
            // snapshots until it has populated every game slot.
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
        var pids = Enumerable.Range(0, room.MaxMembers)
            .Select(slot => (object?)(members.FirstOrDefault(m => m.RoomEntryNo == slot)?.Pid ?? -1))
            .ToArray();
        // Nothing captures a client's refresh rate yet: CMD_SET_CURRENTPLAYER carries
        // frame_rate but the account handler does not read it. 60Hz for every slot until
        // that is wired through.
        var video = KvArray.Repeat("FRAME_60", room.MaxMembers);

        var sent = 0;
        foreach (var member in members)
        {
            var rqid = member.DecideGamePlayerWatchRqid ?? UnsolicitedRqid;
            var update = KvMessage.Ok("CMD_WATCH_DECIDE_GAMEPLAYER", rqid)
                .Set("status", room.Status)
                .Set("player_count", members.Count)
                .Set("pid", new KvArray(pids))
                .Set("video", video);
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

    public KvMessage? BuildRoomStateUpdate(Room room, Session member, string eventName = "StateChanged")
        => KvMessage.Ok("CMD_WATCH_ROOMSTATE", member.RoomStateWatchRqid ?? UnsolicitedRqid)
            .Set("event", eventName)
            .Set("owner_pid", room.OwnerPid)
            .Set("state", room.Status);

    public int PublishStartableGameEntrySnapshots()
    {
        var sent = 0;
        foreach (var room in Rooms.Where(room => room.IsStartable))
            sent += PublishGameEntryChanged(room);
        return sent;
    }

    public int PublishRoomStateSnapshots()
    {
        var sent = 0;
        foreach (var room in Rooms.Where(room => !room.IsEmpty))
            sent += PublishRoomStateChanged(room);
        return sent;
    }

    private void PublishRoomDeleted(Room room, Guid? except)
    {
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
        => BroadcastToBlock(
            member,
            KvMessage.Ok("MSG_BLOCKPLAYERLISTADD", UnsolicitedRqid).Set("block_player", entry));

    /// <summary>Removes a player from every other cached roster in the block.</summary>
    /// <remarks>FUN_0106B920 reads a single "pid" for MSG_BLOCKPLAYERLISTDEL.</remarks>
    public int PublishBlockPlayerRemoved(Session member)
        => member.Pid <= 0
            ? 0
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
        // so delivery cannot depend on a per-request subscription flag. InRoom sessions
        // are excluded because their connection has left the room-browser parser.
        foreach (var subscriber in _sessions.Values.Where(session =>
                     session.State == SessionState.InBlock
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

        if (channel == TextChatChannel.Room)
        {
            var roomId = sender.RoomId
                         ?? senderSessions.FirstOrDefault(s => s.RoomId is not null)?.RoomId;

            if (roomId is { } id && FindRoom(id) is { } room)
                return ($"room {id}", room.Snapshot().Select(s => s.Pid).Distinct().ToArray());

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
