// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;
using OpenEleven.Data;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Pages;

/// <summary>The in-game ranking tabs, all-time or this week, per division where the title has them.</summary>
[OutputCache(Duration = 30)]
public sealed class RankingsModel(GameDbContext db, Hub hub, GameProfile game) : PageModel
{
    /// <summary>Tab name to the in-game ranking type (RankSystem.Metric).</summary>
    public static readonly (string Key, string Label, int Type)[] Tabs =
    [
        ("rating", "Rating", 6), ("wins", "Wins", 2), ("matches", "Matches", 1), ("winpct", "Win %", 7), ("streak", "Streak", 5),
    ];

    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public bool Week { get; set; }
    [BindProperty(SupportsGet = true)] public string? Division { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public bool Ranked => RankSystem.Applies(game);
    public IEnumerable<(string Key, string Label, int Type)> Shown => Tabs.Where(t => Ranked || t.Key != "rating");
    public IReadOnlyList<RankSystem.Line> Lines { get; private set; } = [];
    public int First { get; private set; }
    public HashSet<int> Online { get; private set; } = [];
    public Pager Pager { get; private set; } = new(1, 1, _ => "");

    public async Task OnGetAsync(CancellationToken ct)
    {
        var tab = Shown.FirstOrDefault(t => t.Key == Sort);
        if (tab.Key is null)
            tab = Shown.First();
        Sort = tab.Key;
        Division = Ranked && RankSystem.Divisions.Contains(Division) ? Division : null;

        var all = await new RankingQuery(db).OrderedAsync(Week ? tab.Type + 8 : tab.Type, Division, ct);
        var pages = Pager.Count(all.Count, SiteData.PageSize);
        PageNumber = Math.Clamp(PageNumber, 1, pages);
        First = (PageNumber - 1) * SiteData.PageSize;
        Lines = all.Skip(First).Take(SiteData.PageSize).ToList();
        Online = SiteData.OnlinePids(hub);
        Pager = new Pager(PageNumber, pages, n => Link(Sort, Week, Division, n));
    }

    public static string Link(string? sort, bool week, string? division, int page = 1)
        => $"/rankings?sort={sort}{(week ? "&week=true" : "")}{(division is null ? "" : $"&division={division}")}{(page > 1 ? $"&p={page}" : "")}";
}
