// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Pages;

/// <summary>Every finished match, newest first.</summary>
[OutputCache(Duration = 30)]
public sealed class MatchesModel(GameDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public IReadOnlyList<MatchRow> List { get; private set; } = [];
    public Pager Pager { get; private set; } = new(1, 1, _ => "");

    public async Task OnGetAsync(CancellationToken ct)
    {
        var pages = Pager.Count(await db.Matches.CountAsync(m => m.Completed, ct), SiteData.PageSize);
        PageNumber = Math.Clamp(PageNumber, 1, pages);
        List = await SiteData.NameAsync(db,
            SiteData.Finished(db).Skip((PageNumber - 1) * SiteData.PageSize).Take(SiteData.PageSize), ct);
        Pager = new Pager(PageNumber, pages, n => $"/matches?p={n}");
    }
}
