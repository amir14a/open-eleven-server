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
    public string TeamName { get; set; } = "";
    public string ShortTeamName { get; set; } = "";
    public string LegendsVsPlayer { get; set; } = "NO";

    /// <summary>The three team_flag entries the creator chose, echoed in the room list.</summary>
    public KvArray TeamFlag { get; set; } = new("YES", "YES", "NO");

    /// <summary>The owner's DLC version when the room was made (room list dlcontent).</summary>
    public string DlcVersion { get; set; } = "";

    /// <summary>Best combination play reported in this room so far.</summary>
    public int TotalCombination { get; set; }
    public int MaxCombination { get; set; }

    /// <summary>Shared by every member once CMD_GET_MATCHID has been asked (0 = not yet).</summary>
    public int MatchId { get; set; }

    /// <summary>Serializes the first CMD_GET_MATCHID of each player, which opens the match record.</summary>
    public SemaphoreSlim MatchIdGate { get; } = new(1, 1);

    /// <summary>Each player's login para_hash, taken when the match starts (anti-farming check).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<int, string> ParaHashes { get; } = new();

    /// <summary>
    /// Set once the host has sent CMD_SET_GAMEMEMBERENV (sides decided). From then on the
    /// clients' own CMD_ENTRY_GAME entry=FALSE is part of leaving the entry screen, not a
    /// withdrawal, and must not clear the entries.
    /// </summary>
    public bool MatchStarting { get; set; }

    /// <summary>Goals reported with CMD_ADD_SCORE during the current match, penalty shoot-out excluded.</summary>
    public int HomeGoals { get; set; }

    public int AwayGoals { get; set; }

    /// <summary>Phase the host last reported (enum 0x1a), so goals land in the right period.</summary>
    public string Phase { get; set; } = "ENTRY";

    /// <summary>Per-period goals, events and combinations of the current match.</summary>
    public MatchSheet Sheet { get; set; } = new();

    /// <summary>Teams of the current match (CMD_SET_GAMETEAM).</summary>
    public int HomeTeamId { get; set; }

    public int AwayTeamId { get; set; }

    /// <summary>Set once the current match has been written to the match history.</summary>
    public bool ResultRecorded { get; set; }

    /// <summary>The player who reported a forfeit (it lost its peer); the other is the suspect.</summary>
    public int? ForfeitStayerPid { get; set; }

    /// <summary>Points and rating each player got from the last match, for CMD_GET_GAMEADDPOINT.</summary>
    public IReadOnlyDictionary<int, PlayerResult> LastResults { get; set; } = new Dictionary<int, PlayerResult>();

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
        // Back in the room screen: the next match starts from scratch.
        if (status is "WAITING" or "SETENV")
        {
            MatchStarting = false;
            MatchId = 0;
        }

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
