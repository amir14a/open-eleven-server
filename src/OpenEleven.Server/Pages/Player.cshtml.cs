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

/// <summary>
/// One player's card. Shows the in-game name only: the account's Game ID is the login name,
/// and publishing it would hand out half of every credential.
/// </summary>
[OutputCache(Duration = 30)]
public sealed class PlayerModel(GameDbContext db, Hub hub, GameProfile game) : PageModel
{
    public bool Ranked => RankSystem.Applies(game);
    public Player Profile { get; private set; } = null!;
    public DateTime MemberSince { get; private set; }
    public bool IsOnline { get; private set; }
    public IReadOnlyList<MatchRow> Recent { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int pid, CancellationToken ct)
    {
        var player = await db.Players.Include(p => p.Stats).Include(p => p.Account)
            .FirstOrDefaultAsync(p => p.Pid == pid, ct);
        if (player is null)
            return NotFound();

        Profile = player;
        MemberSince = player.Account?.CreatedAt ?? default;
        IsOnline = SiteData.OnlinePids(hub).Contains(pid);
        Recent = await SiteData.NameAsync(db,
            SiteData.Finished(db).Where(m => m.HomePid == pid || m.AwayPid == pid).Take(15), ct);
        return Page();
    }
}
