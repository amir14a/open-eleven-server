// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

public sealed class Room
{
    private readonly List<Session> _members = new();
    private readonly Lock _gate = new();

    public Room(
        int id,
        string name,
        int ownerPid,
        int maxMembers,
        ServiceRole serviceRole,
        int blockId)
    {
        Id = id;
        Name = name;
        OwnerPid = ownerPid;
        MaxMembers = maxMembers;
        ServiceRole = serviceRole;
        BlockId = blockId;
    }

    public int Id { get; }
    public string Name { get; set; }
    public int OwnerPid { get; private set; }
    public int MaxMembers { get; }
    public ServiceRole ServiceRole { get; }
    public int BlockId { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public string Status { get; private set; } = "WAITING";

    public bool IsStartable => Status is "WAITING" or "SETENV";

    /// <summary>
    /// Match settings from CMD_SET_GAMEENV (half length, injuries, ball, substitutions).
    /// Held verbatim: the client owns this shape, and it has to survive unchanged until
    /// it is handed to whoever needs it — the joining peer, or the match record.
    /// </summary>
    public KvMessage? GameEnvironment { get; set; }

    /// <summary>Room options the client sends at creation time.</summary>
    public string MatchType { get; set; } = "";
    public string TeamCategory { get; set; } = "";
    public string Language { get; set; } = "";
    public bool AllowGuest { get; set; } = true;
    public bool InviteLimited { get; set; }
    public string Password { get; set; } = "";

    public int Count
    {
        get { lock (_gate) return _members.Count; }
    }

    public bool IsEmpty => Count == 0;

    public RoomJoinResult TryJoin(Session session)
    {
        lock (_gate)
        {
            if (_members.Any(m => m.Id == session.Id || (session.Pid > 0 && m.Pid == session.Pid)))
                return new RoomJoinResult(RoomJoinStatus.AlreadyInRoom, this, []);

            if (_members.Count >= MaxMembers)
                return new RoomJoinResult(RoomJoinStatus.Full, this, []);

            var existingMembers = _members.ToArray();
            session.RoomEntryNo = NextFreeEntryNo();
            _members.Add(session);
            return new RoomJoinResult(RoomJoinStatus.Joined, this, existingMembers);
        }
    }

    public RoomRemoval Remove(Session session)
    {
        lock (_gate)
        {
            if (_members.RemoveAll(m => m.Id == session.Id) == 0)
                return new RoomRemoval(false, false, _members.ToArray());

            var ownerChanged = false;
            if (session.Pid == OwnerPid && _members.Count > 0)
            {
                OwnerPid = _members.MinBy(m => m.RoomEntryNo)!.Pid;
                ownerChanged = true;
            }

            session.RoomEntryNo = -1;
            session.GameEntryNo = -1;
            session.GameSide = -1;
            session.GameEntryWatchRqid = null;
            session.RoomStateWatchRqid = null;
            session.DecideGameEnvWatchRqid = null;
            session.DecideGamePlayerWatchRqid = null;
            session.DecideGamePlayerEnvWatchRqid = null;
            session.DisconPlayerEnvWatchRqid = null;
            session.DisconPlayerMatchWatchRqid = null;
            session.UpdateGameRecordWatchRqid = null;
            session.HasGuestPlayer = false;
            return new RoomRemoval(true, ownerChanged, _members.ToArray());
        }
    }

    public bool TrySetStatus(string status)
    {
        if (status is not ("WAITING" or "SETENV" or "GAME" or "RESULT" or
            "DISCONWAIT" or "RESULTWAIT"))
            return false;

        Status = status;
        return true;
    }

    public bool SetGameEntry(Session session, bool entered, int side)
    {
        lock (_gate)
        {
            if (_members.All(member => member.Id != session.Id))
                return false;

            if (!entered)
            {
                session.GameEntryNo = -1;
                session.GameSide = -1;
                return true;
            }

            if (session.GameEntryNo < 0)
                session.GameEntryNo = NextFreeGameEntryNo();
            session.GameSide = side;
            return true;
        }
    }

    /// <summary>Copy so callers can iterate without holding the lock.</summary>
    public IReadOnlyList<Session> Snapshot()
    {
        lock (_gate) return _members.ToArray();
    }

    private int NextFreeEntryNo()
    {
        for (var n = 0; n < MaxMembers; n++)
            if (_members.All(m => m.RoomEntryNo != n))
                return n;
        return _members.Count;
    }

    private int NextFreeGameEntryNo()
    {
        for (var n = 0; n < MaxMembers; n++)
            if (_members.All(member => member.GameEntryNo != n))
                return n;
        return _members.Count(member => member.GameEntryNo >= 0);
    }
}

public enum RoomJoinStatus
{
    Joined,
    RoomNotFound,
    Full,
    AlreadyInRoom,
    InvalidRoomInfo,
    WrongPassword,
}

public readonly record struct RoomJoinResult(
    RoomJoinStatus Status,
    Room? Room,
    IReadOnlyList<Session> ExistingMembers);

public enum RoomLeaveStatus
{
    Left,
    NotInRoom,
    RoomNotFound,
}

public readonly record struct RoomLeaveResult(
    RoomLeaveStatus Status,
    int RoomId,
    bool RoomDeleted,
    bool OwnerChanged);

public readonly record struct RoomRemoval(
    bool Removed,
    bool OwnerChanged,
    IReadOnlyList<Session> RemainingMembers);
