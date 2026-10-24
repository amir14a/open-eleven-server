// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;

namespace OpenEleven.Server.State;

/// <summary>A player searching for a quick match (MSG_QUICK_REQ_MATCHING_START).</summary>
/// <param name="Rqid">The search request's rqid; every MSG_QUICK_* push of this search reuses it.</param>
/// <param name="Key">Settings that must agree for two players to be paired.</param>
public sealed record QuickSeeker(Session Session, int Rqid, string Type, int PlayerNum, string GameTime, string Frrate, string Key)
{
    /// <summary>The player's own language, and the opponent language it asked for (enum 0x1c, ANY = no preference).</summary>
    public string Lang { get; init; } = "";
    public string OppLang { get; init; } = "ANY";

    public int Rating { get; init; }

    /// <summary>The player's quick match option asks for opponents of similar rating.</summary>
    public bool RatingFilter { get; init; }

    /// <summary>Largest rating gap a rating-filtered search accepts.</summary>
    public const int RatingWindow = 200;

    /// <summary>
    /// Both language wishes hold, a rating filter on either side holds, and the game data
    /// matches (same rule as joining a room).
    /// </summary>
    public bool CompatibleWith(QuickSeeker other)
        => LangOk(OppLang, other.Lang) && LangOk(other.OppLang, Lang)
           && (!(RatingFilter || other.RatingFilter) || Math.Abs(Rating - other.Rating) <= RatingWindow)
           && (Session.ParaHash.Length == 0 || other.Session.ParaHash.Length == 0
               || string.Equals(Session.ParaHash, other.Session.ParaHash, StringComparison.OrdinalIgnoreCase));

    private static bool LangOk(string wanted, string actual)
        => wanted is "" or "ANY" || actual.Length == 0 || string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Players paired by the matchmaker, from MSG_QUICK_MATCH_MEMBER_NOTICE until their room exists.
/// The first member is the owner, who hosts the match.
/// </summary>
public sealed class QuickGroup(IReadOnlyList<QuickSeeker> members)
{
    private readonly HashSet<int> _replied = [];
    private readonly HashSet<int> _accepted = [];

    public IReadOnlyList<QuickSeeker> Members { get; } = members;

    /// <summary>MSG_QUICK_MATCHING_RES has gone out, so a cancel is reported on the offer reply.</summary>
    public bool Offered { get; private set; }

    /// <summary>True once, for the MSG_QUICK_MATCH_MEMBER_REPLY that completes the group.</summary>
    public bool Replied(int pid)
    {
        lock (_replied)
        {
            if (!_replied.Add(pid) || _replied.Count < Members.Count)
                return false;
            Offered = true;
            return true;
        }
    }

    /// <summary>True once, for the MSG_QUICK_ACCEPT_MATCH_OFFER that completes the group.</summary>
    public bool Accepted(int pid)
    {
        lock (_accepted)
            return _accepted.Add(pid) && _accepted.Count == Members.Count;
    }

    /// <summary>Tells the members other than <paramref name="leaver"/> that the match fell through.</summary>
    public void TellCancelled(Session leaver)
    {
        var msg = Offered ? "MSG_QUICK_ACCEPT_MATCH_OFFER_RES" : "MSG_QUICK_MATCHING_RES";
        foreach (var m in Members.Where(m => m.Session != leaver))
            m.Session.Push(KvMessage.Err(msg, m.Rqid, "ERR_QMATCH_CANCELED"));
    }
}

/// <summary>
/// PES 2011 quick match: who is searching, and the groups formed from them. Players are paired
/// first come, first served among those with the same <see cref="QuickSeeker.Key"/>.
/// </summary>
public sealed class QuickMatch
{
    private readonly Lock _gate = new();
    private readonly List<QuickSeeker> _waiting = [];
    private readonly List<QuickGroup> _groups = [];

    /// <summary>Adds the player to the search; returns the group once enough compatible players wait.</summary>
    public QuickGroup? Search(QuickSeeker seeker)
    {
        lock (_gate)
        {
            RemoveLocked(s => s.Pid == seeker.Session.Pid);
            var peers = new List<QuickSeeker>();
            foreach (var w in _waiting.Where(w => w.Key == seeker.Key && w.CompatibleWith(seeker)))
            {
                if (peers.Count == seeker.PlayerNum - 1)
                    break;
                if (peers.All(p => p.CompatibleWith(w)))
                    peers.Add(w);
            }

            if (peers.Count < seeker.PlayerNum - 1)
            {
                _waiting.Add(seeker);
                return null;
            }

            foreach (var peer in peers)
                _waiting.Remove(peer);
            var group = new QuickGroup([.. peers, seeker]);
            _groups.Add(group);
            return group;
        }
    }

    public QuickGroup? GroupOf(Session session)
    {
        lock (_gate)
            return _groups.FirstOrDefault(g => g.Members.Any(m => m.Session == session));
    }

    /// <summary>Ends the player's search or forming group; returns the group so the others can be told.</summary>
    public QuickGroup? Leave(Session session)
    {
        lock (_gate)
            return RemoveLocked(s => s == session);
    }

    /// <summary>The group's room exists; the matchmaker is done with it.</summary>
    public void Complete(QuickGroup group)
    {
        lock (_gate)
            _groups.Remove(group);
    }

    public int Searching(string type, int playerNum)
    {
        lock (_gate)
            return _waiting.Count(w => w.Type == type && w.PlayerNum == playerNum);
    }

    private QuickGroup? RemoveLocked(Func<Session, bool> match)
    {
        _waiting.RemoveAll(w => match(w.Session));
        var group = _groups.FirstOrDefault(g => g.Members.Any(m => match(m.Session)));
        if (group is not null)
            _groups.Remove(group);
        return group;
    }
}
