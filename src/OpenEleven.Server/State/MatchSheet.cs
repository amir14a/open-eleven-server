// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.State;

/// <summary>
/// What the match host reports during a match, beyond the score: goals per period (the
/// history screen shows 1st/2nd/extra time/penalties separately), goal and card events,
/// and combination play. Reset when the first half starts.
/// </summary>
public sealed class MatchSheet
{
    /// <summary>Periods in the order CMD_GET_GAMERESULTS lists them.</summary>
    public static readonly string[] Periods = ["1ST", "2ND", "EX1ST", "EX2ND", "PK"];

    private readonly object _gate = new();
    private readonly int[] _home = new int[5];
    private readonly int[] _away = new int[5];
    private readonly List<string> _events = [];

    public int TotalCombination { get; private set; }
    public int MaxCombination { get; private set; }

    /// <summary>Who reported the combinations (the host); they count for that player.</summary>
    public int CombinationPid { get; private set; }

    /// <summary>Index into <see cref="Periods"/> for a game phase; breaks count toward the half before.</summary>
    public static int PeriodOf(string phase) => phase switch
    {
        "2ND" or "BREAK2" => 1,
        "EX1ST" or "BREAK3" => 2,
        "EX2ND" or "BREAK4" => 3,
        "PK" or "END" => 4,
        _ => 0,
    };

    public void Goal(bool away, string phase, string details)
    {
        lock (_gate)
        {
            (away ? _away : _home)[PeriodOf(phase)]++;
            _events.Add($"GOAL|{phase}|{(away ? "AWAY" : "HOME")}|{details}");
        }
    }

    public void Event(string line)
    {
        lock (_gate)
            _events.Add(line);
    }

    public void Combination(int pid, int total, int max)
    {
        lock (_gate)
        {
            CombinationPid = pid;
            TotalCombination = total;
            MaxCombination = Math.Max(MaxCombination, max);
        }
    }

    public int[] Home { get { lock (_gate) return (int[])_home.Clone(); } }
    public int[] Away { get { lock (_gate) return (int[])_away.Clone(); } }
    public string Events { get { lock (_gate) return string.Join('\n', _events); } }

    public bool ExtraTime { get { lock (_gate) return _home[2] + _home[3] + _away[2] + _away[3] > 0 || ReachedExtraTime; } }
    public bool Penalties { get { lock (_gate) return _home[4] + _away[4] > 0 || ReachedPenalties; } }

    /// <summary>Set from the phases, so a goalless extra time or shoot-out still shows.</summary>
    public bool ReachedExtraTime { get; set; }
    public bool ReachedPenalties { get; set; }

    public static string Format(int[] periods) => string.Join(',', periods);

    public static int[] Parse(string periods)
    {
        var parts = periods.Split(',');
        var result = new int[5];
        for (var i = 0; i < result.Length && i < parts.Length; i++)
            int.TryParse(parts[i], out result[i]);
        return result;
    }
}
