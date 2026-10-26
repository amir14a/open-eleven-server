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
    [BindProperty(SupportsGet = true, Name = "size")] public int Size { get; set; }

    public IReadOnlyList<MatchRow> List { get; private set; } = [];
    public Pager Pager { get; private set; } = new(1, 1, _ => "");

    public async Task OnGetAsync(CancellationToken ct)
    {
        var size = SiteData.Size(Size);
        var pages = Pager.Count(await db.Matches.CountAsync(m => m.Completed, ct), size);
        PageNumber = Math.Clamp(PageNumber, 1, pages);
        List = await SiteData.NameAsync(db,
            SiteData.Finished(db).Skip((PageNumber - 1) * size).Take(size), ct);
        Pager = new Pager(PageNumber, pages, n => Link(n, size)) { Size = size, SizeHref = s => Link(1, s) };
    }

    private static string Link(int page, int size)
        => size == SiteData.PageSize ? $"/matches?p={page}" : $"/matches?p={page}&size={size}";
}
