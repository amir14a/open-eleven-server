// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Pages;

/// <summary>Totals, who is online, what is being played and the latest results.</summary>
[OutputCache(Duration = 15)]
public sealed class IndexModel(GameDbContext db, Hub hub, GameProfile game, IOptionsMonitor<ServerOptions> options) : PageModel
{
    public int Players { get; private set; }
    public int Matches { get; private set; }
    public int Online { get; private set; }
    public string Announcement { get; private set; } = "";
    public bool Ranked => RankSystem.Applies(game);
    public IReadOnlyList<LiveMatch> Live { get; private set; } = [];
    public IReadOnlyList<RankSystem.Line> Top { get; private set; } = [];
    public IReadOnlyList<MatchRow> Recent { get; private set; } = [];
    public IReadOnlyList<InformationItem> News { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Players = await db.Players.CountAsync(ct);
        Matches = await db.Matches.CountAsync(m => m.Completed, ct);
        Online = hub.ConnectedPlayerCount();
        Announcement = options.CurrentValue.Site.Announcement;
        Live = SiteData.LiveMatches(hub);
        Top = (await new RankingQuery(db).OrderedAsync(Ranked ? 6 : 2, null, ct)).Take(5).ToList();
        Recent = await SiteData.NameAsync(db, SiteData.Finished(db).Take(8), ct);
        News = await db.Information.Where(i => i.Enabled).OrderByDescending(i => i.PublishedAt).Take(4).ToListAsync(ct);
    }
}
