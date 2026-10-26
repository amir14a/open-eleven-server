// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Pages.Admin;

/// <summary>One account: its players, and ban, kick and password reset.</summary>
[SensitiveBody]
public sealed class AccountModel(GameDbContext db, Hub hub, PendingLoginStore logins, ILogger<AccountModel> log) : PageModel
{
    public const int MaxReasonLength = 255;

    public Account Account { get; private set; } = null!;
    public int Connections { get; private set; }

    [TempData] public string? Message { get; set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
        => await LoadAsync(id, ct) ? Page() : NotFound();

    public async Task<IActionResult> OnPostBanAsync(int id, string? reason, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct))
            return NotFound();

        Account.Banned = true;
        Account.BanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, MaxReasonLength)];
        await db.SaveChangesAsync(ct);
        var closed = hub.Disconnect(id);
        log.LogWarning("Admin {Admin} banned {GameId}: {Reason}", User.Identity?.Name, Account.GameId, Account.BanReason);
        return Done($"{Account.GameId} is banned{(closed > 0 ? " and was disconnected" : "")}.");
    }

    public async Task<IActionResult> OnPostUnbanAsync(int id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct))
            return NotFound();

        Account.Banned = false;
        Account.BanReason = null;
        await db.SaveChangesAsync(ct);
        log.LogWarning("Admin {Admin} unbanned {GameId}", User.Identity?.Name, Account.GameId);
        return Done($"{Account.GameId} can play again.");
    }

    public async Task<IActionResult> OnPostKickAsync(int id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct))
            return NotFound();

        var closed = hub.Disconnect(id);
        log.LogWarning("Admin {Admin} kicked {GameId} ({Count} connections)", User.Identity?.Name, Account.GameId, closed);
        return Done($"Disconnected {Account.GameId} ({closed} connections).");
    }

    public async Task<IActionResult> OnPostPasswordAsync(int id, string? password, string? confirm, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct))
            return NotFound();

        // The same rules as registration: the game has to be able to reproduce the digest.
        if (RegistrationEndpoint.ValidatePassword(password, confirm) is { } problem)
        {
            Error = problem;
            return Page();
        }

        Account.PasswordHash = AuthProof.HashPassword(password!);
        await db.SaveChangesAsync(ct);
        // A login made with the old password must not outlive it.
        logins.Revoke(id);
        var closed = hub.Disconnect(id);
        log.LogWarning("Admin {Admin} set a new password for {GameId}", User.Identity?.Name, Account.GameId);
        return Done($"New password set for {Account.GameId}{(closed > 0 ? "; they were disconnected" : "")}.");
    }

    private async Task<bool> LoadAsync(int id, CancellationToken ct)
    {
        var account = await db.Accounts.Include(a => a.Players).ThenInclude(p => p.Stats)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
            return false;

        Account = account;
        Connections = hub.SessionsForAccount(id).Count;
        return true;
    }

    private RedirectToPageResult Done(string message)
    {
        Message = message;
        return RedirectToPage(new { id = Account.Id });
    }
}
