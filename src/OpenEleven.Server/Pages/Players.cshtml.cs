// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Pages;

/// <summary>Every player, online ones first, searchable by name.</summary>
[OutputCache(Duration = 30)]
public sealed class PlayersModel(GameDbContext db, Hub hub, GameProfile game) : PageModel
{
    public const int MaxSearchLength = 32;   // the form's maxlength; a longer query matches no name anyway

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    public bool Ranked => RankSystem.Applies(game);
    public IReadOnlyList<Player> List { get; private set; } = [];
    public HashSet<int> Online { get; private set; } = [];
    public Pager Pager { get; private set; } = new(1, 1, _ => "");

    public async Task OnGetAsync(CancellationToken ct)
    {
        Online = SiteData.OnlinePids(hub);
        var query = db.Players.Include(p => p.Stats).AsQueryable();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var term = Q.Trim().ToUpperInvariant();
            if (term.Length > MaxSearchLength)
                term = term[..MaxSearchLength];
            query = query.Where(p => p.NormalizedName.Contains(term));
        }

        var online = Online.ToList();
        var pages = Pager.Count(await query.CountAsync(ct), SiteData.PageSize);
        PageNumber = Math.Clamp(PageNumber, 1, pages);
        List = await query
            .OrderByDescending(p => online.Contains(p.Pid))
            .ThenByDescending(p => p.LastPlayedAt)
            .ThenBy(p => p.Name)
            .Skip((PageNumber - 1) * SiteData.PageSize).Take(SiteData.PageSize)
            .ToListAsync(ct);
        var q = string.IsNullOrWhiteSpace(Q) ? "" : $"q={Uri.EscapeDataString(Q.Trim())}&";
        Pager = new Pager(PageNumber, pages, n => $"/players?{q}page={n}");
    }
}
