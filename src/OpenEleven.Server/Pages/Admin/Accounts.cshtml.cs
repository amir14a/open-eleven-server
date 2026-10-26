// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Pages.Admin;

/// <summary>Accounts by Game ID or player name, newest first; banned ones on their own.</summary>
public sealed class AccountsModel(GameDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public bool Banned { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    public IReadOnlyList<Account> List { get; private set; } = [];
    public Pager Pager { get; private set; } = new(1, 1, _ => "");

    public async Task OnGetAsync(CancellationToken ct)
    {
        var query = db.Accounts.Include(a => a.Players).AsQueryable();
        if (Banned)
            query = query.Where(a => a.Banned);
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var term = Q.Trim();
            var upper = term.ToUpperInvariant();
            query = query.Where(a => a.GameId.Contains(term) || a.Players.Any(p => p.NormalizedName.Contains(upper)));
        }

        var pages = Pager.Count(await query.CountAsync(ct), SiteData.PageSize);
        PageNumber = Math.Clamp(PageNumber, 1, pages);
        List = await query.OrderByDescending(a => a.CreatedAt)
            .Skip((PageNumber - 1) * SiteData.PageSize).Take(SiteData.PageSize).ToListAsync(ct);
        var filter = $"{(string.IsNullOrWhiteSpace(Q) ? "" : $"q={Uri.EscapeDataString(Q.Trim())}&")}{(Banned ? "banned=true&" : "")}";
        Pager = new Pager(PageNumber, pages, n => $"/admin/accounts?{filter}page={n}");
    }
}
