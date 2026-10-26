// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Web;

/// <summary>A finished match with both players' names.</summary>
public sealed record MatchRow(MatchRecord Match, string Home, string Away);

/// <summary>A match being played right now.</summary>
public sealed record LiveMatch(string Home, string Away, int HomeGoals, int AwayGoals, string Phase);

/// <summary>One page of a long list; <see cref="Href"/> builds the link to another page.</summary>
public sealed record Pager(int Page, int Pages, Func<int, string> Href)
{
    public static int Count(int total, int size) => Math.Max(1, (total + size - 1) / size);
}

/// <summary>Queries shared by the public pages.</summary>
public static class SiteData
{
    public const int PageSize = 50;

    /// <summary>Completed matches, newest first.</summary>
    public static IQueryable<MatchRecord> Finished(GameDbContext db)
        => db.Matches.Where(m => m.Completed).OrderByDescending(m => m.PlayedAt);

    /// <summary>Runs <paramref name="query"/> and names both sides; a deleted player stays visible as such.</summary>
    public static async Task<List<MatchRow>> NameAsync(GameDbContext db, IQueryable<MatchRecord> query, CancellationToken ct)
    {
        var matches = await query.ToListAsync(ct);
        var pids = matches.SelectMany(m => new[] { m.HomePid, m.AwayPid }).Distinct().ToList();
        var names = await db.Players.Where(p => pids.Contains(p.Pid)).ToDictionaryAsync(p => p.Pid, p => p.Name, ct);
        string Name(int pid) => names.GetValueOrDefault(pid, "(deleted)");
        return matches.Select(m => new MatchRow(m, Name(m.HomePid), Name(m.AwayPid))).ToList();
    }

    /// <summary>Players with a selected profile on any service.</summary>
    public static HashSet<int> OnlinePids(Hub hub)
        => hub.Sessions.Where(s => s.Pid > 0 && s.State >= SessionState.PlayerSelected).Select(s => s.Pid).ToHashSet();

    /// <summary>Rooms whose match has started, home side first.</summary>
    public static List<LiveMatch> LiveMatches(Hub hub)
        => hub.Rooms.Where(r => r.Status == "GAME").Select(r =>
        {
            var members = r.Snapshot();
            string Side(int side) => members.FirstOrDefault(s => s.GameSide == side)?.PlayerName
                                     ?? members.ElementAtOrDefault(side)?.PlayerName ?? "?";
            return new LiveMatch(Side(0), Side(1), r.HomeGoals, r.AwayGoals, r.Phase);
        }).ToList();

    public static string Date(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string WinPercent(int wins, int matches) => matches == 0 ? "–" : $"{wins * 100.0 / matches:0.#}%";
}
