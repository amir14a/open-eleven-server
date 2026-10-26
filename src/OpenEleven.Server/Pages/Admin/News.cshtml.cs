// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Data.Entities;

namespace OpenEleven.Server.Pages.Admin;

/// <summary>
/// The news the game shows after login (CMD_GET_INFORMATIONLIST) and the home page lists.
/// An important item pops up once per player.
/// </summary>
public sealed class NewsModel(GameDbContext db, ILogger<NewsModel> log) : PageModel
{
    public const int MaxSubject = 64;
    public const int MaxBody = 512;

    public IReadOnlyList<InformationItem> Items { get; private set; } = [];

    /// <summary>The item in the form: a new one, or the one being edited.</summary>
    [BindProperty] public InformationItem Item { get; set; } = new();

    [TempData] public string? Message { get; set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? edit, CancellationToken ct)
    {
        if (edit is { } id)
        {
            if (await db.Information.FindAsync([id], ct) is not { } item)
                return NotFound();
            Item = item;
        }

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        Item.Subject = Item.Subject.Trim();
        Item.Body = Item.Body.Trim();
        if (Item.Subject.Length is 0 or > MaxSubject || Item.Body.Length > MaxBody)
        {
            Error = $"A subject of 1–{MaxSubject} characters is required, and the text can be at most {MaxBody}.";
            await LoadAsync(ct);
            return Page();
        }

        if (Item.Id == 0)
        {
            db.Information.Add(new InformationItem
            {
                Subject = Item.Subject, Body = Item.Body, Important = Item.Important, Present = true,
                Enabled = Item.Enabled, PublishedAt = DateTime.UtcNow,
            });
        }
        else if (await db.Information.FindAsync([Item.Id], ct) is { } stored)
        {
            (stored.Subject, stored.Body, stored.Important, stored.Enabled) = (Item.Subject, Item.Body, Item.Important, Item.Enabled);
        }
        else
        {
            return NotFound();
        }

        await db.SaveChangesAsync(ct);
        log.LogWarning("Admin {Admin} saved news '{Subject}'", User.Identity?.Name, Item.Subject);
        Message = $"Saved “{Item.Subject}”.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        if (await db.Information.FindAsync([id], ct) is { } item)
        {
            db.Information.Remove(item);
            await db.SaveChangesAsync(ct);
            log.LogWarning("Admin {Admin} deleted news '{Subject}'", User.Identity?.Name, item.Subject);
            Message = $"Deleted “{item.Subject}”.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
        => Items = await db.Information.OrderByDescending(i => i.PublishedAt).ToListAsync(ct);
}
