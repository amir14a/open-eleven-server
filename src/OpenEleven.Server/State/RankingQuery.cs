// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>
/// Answers the ranking screen. The client (FUN_005f0a40, request object FUN_011c02a0) fetches
/// the RANK url over HTTP with the query as POST body:
/// type=&lt;t&gt;&amp;division=&lt;d&gt;&amp;from=&lt;n&gt;&amp;records=100[&amp;weekid=&lt;w&gt;]&amp;pid=&lt;me&gt;&amp;flag=&lt;f&gt;
/// and wraps the body it gets back into ranking_data="...".
/// <list type="bullet">
/// <item>type 1..7, no weekid: all-time table (tabs: 1 matches, 2 wins, 3 draws, 4 losses,
/// 5 streak, 6 rating, 7 win %).</item>
/// <item>type 9..15, no weekid: the running week (live counters), same layout as all-time.</item>
/// <item>weekid present (sent to the RANK_WEEK url): an archived week, weekly layout.</item>
/// </list>
/// division 0..4 = D5..D1 (PES 2010: D3C..D1), anything else = every division; flag=1 = return
/// the page holding pid.
/// </summary>
public sealed class RankingQuery(GameDbContext db, GameProfile profile = GameProfile.Pes2011Pc)
{
    public async Task<string> RenderAsync(string data, DateTime now, CancellationToken ct = default)
    {
        var q = data.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0]).ToDictionary(g => g.Key, g => g.Last()[1]);
        int Arg(string key, int fallback) => q.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

        var type = Arg("type", 1);
        var division = Arg("division", 6);
        var from = Math.Max(0, Arg("from", 0));
        var records = Math.Clamp(Arg("records", 100), 1, 100);
        var me = Arg("pid", 0);
        string? divisionName = division is >= 0 and <= 4 ? RankSystem.RankingDivisions(profile)[division] : null;

        List<RankSystem.Line> ordered;
        (DateTime, DateTime)? week = null;
        if (q.ContainsKey("weekid"))
        {
            var weeks = await ArchivedWeeksAsync(db, 52, ct);
            var weekId = Arg("weekid", -1);
            var start = weeks.Contains(weekId) ? weekId
                : weekId >= 0 && weekId < weeks.Count ? weeks[weekId]
                : weeks.FirstOrDefault();
            var rows = await db.WeeklyRankings.Where(r => r.WeekStart == start).ToListAsync(ct);
            ordered = Order(InDivision(rows.Select(r => new RankSystem.Line(r.Pid, r.Name, r.Matches, r.Wins, r.Draws,
                r.Losses, r.Points, r.Division, r.Rating, Streak: r.Streak)), divisionName), type);
            var weekStart = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime;
            week = (weekStart, weekStart.AddDays(7).AddSeconds(-1));
        }
        else
        {
            ordered = await OrderedAsync(type, divisionName, ct);
        }

        // "Jump to me": the page that holds the player (FUN_005f02d0 looks for its pid).
        if (q.GetValueOrDefault("flag") == "1" && ordered.FindIndex(l => l.Pid == me) is var mine and >= 0)
            from = mine / 100 * 100;

        var registered = await db.Players.CountAsync(ct);
        return RankSystem.Format(ordered, type, from, records, now, week, registered, profile);
    }

    /// <summary>
    /// The all-time table (type 1..7) or the running week (9..15) in the tab's order, limited to
    /// one division unless <paramref name="divisionName"/> is null. Also feeds the web rankings.
    /// </summary>
    public async Task<List<RankSystem.Line>> OrderedAsync(int type, string? divisionName, CancellationToken ct = default)
    {
        if (type > 8)
        {
            var weekly = await db.Players.Where(p => p.WeeklyMatches > 0).ToListAsync(ct);
            return Order(InDivision(weekly.Select(p => new RankSystem.Line(p.Pid, p.Name, p.WeeklyMatches, p.WeeklyWins,
                p.WeeklyDraws, p.WeeklyLosses, p.WeeklyPoints, p.Division, p.Rating, Streak: p.WeeklyBestStreak)),
                divisionName), type);
        }

        var players = await db.Players.Include(p => p.Stats).Where(p => p.Stats.MatchCount > 0).ToListAsync(ct);
        return Order(InDivision(players.Select(p => new RankSystem.Line(p.Pid, p.Name, p.Stats.MatchCount,
            p.Stats.WinCount, p.Stats.DrawCount, p.Stats.LoseCount, p.Point, p.Division, p.Rating, p.Stats.Goals,
            p.Stats.MaxContinuousWins)), divisionName), type);
    }

    // Matched on the name the client sees: PES 2010 rows may still hold D5 (shown as D3C).
    // ponytail: filters in memory, move it into the query if the player table gets large.
    private IEnumerable<RankSystem.Line> InDivision(IEnumerable<RankSystem.Line> lines, string? divisionName)
        => divisionName == null ? lines : lines.Where(l => RankSystem.Label(l.Division, profile) == divisionName);

    private static List<RankSystem.Line> Order(IEnumerable<RankSystem.Line> lines, int type)
        => lines.OrderByDescending(l => RankSystem.Metric(type, l))
            .ThenByDescending(l => l.Rating).ThenBy(l => l.Pid).ToList();

    /// <summary>Start (unix seconds) of the archived weeks, newest first.</summary>
    public static async Task<List<int>> ArchivedWeeksAsync(GameDbContext db, int count, CancellationToken ct)
        => (await db.WeeklyRankings.Select(r => r.WeekStart).Distinct()
                .OrderByDescending(w => w).Take(count).ToListAsync(ct))
            .Select(w => (int)w).ToList();
}
