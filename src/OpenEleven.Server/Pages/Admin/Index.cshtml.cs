// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Pages.Admin;

/// <summary>Everyone connected right now, the rooms they are in, and a way to drop them.</summary>
public sealed class IndexModel(Hub hub, ILogger<IndexModel> log) : PageModel
{
    public sealed record Connected(int AccountId, string GameId, string Player, string Address, string Services,
        DateTimeOffset Since, int? RoomId);

    public IReadOnlyList<Connected> Accounts { get; private set; } = [];
    public int Anonymous { get; private set; }
    public IReadOnlyList<Room> Rooms { get; private set; } = [];

    [TempData] public string? Message { get; set; }

    public void OnGet()
    {
        var sessions = hub.Sessions;
        Anonymous = sessions.Count(s => s.AccountId == 0);
        Accounts = sessions.Where(s => s.AccountId != 0).GroupBy(s => s.AccountId).Select(g => new Connected(
                g.Key,
                g.First().GameId,
                g.Select(s => s.PlayerName).FirstOrDefault(n => n.Length > 0) ?? "",
                g.First().Remote.Address.ToString(),
                string.Join(", ", g.Select(s => s.Role).Distinct()),
                g.Min(s => s.ConnectedAt),
                g.Select(s => s.RoomId).FirstOrDefault(r => r is not null)))
            .OrderBy(c => c.Since).ToList();
        Rooms = hub.Rooms.OrderBy(r => r.CreatedAt).ToList();
    }

    public IActionResult OnPostKick(int accountId, string? gameId)
    {
        var closed = hub.Disconnect(accountId);
        log.LogWarning("Admin {Admin} kicked {GameId} ({Count} connections)", User.Identity?.Name, gameId, closed);
        Message = $"Disconnected {gameId} ({closed} connections).";
        return RedirectToPage();
    }
}
