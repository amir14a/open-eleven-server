// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.Pages.Admin;

/// <summary>
/// The blocked terms (Protocol.BlockedTerms): refused in room names and checked names, starred
/// out in chat. Saved whole over server.yaml's list and in effect at once.
/// </summary>
public sealed class WordsModel(
    GameDbContext db,
    LiveSettings live,
    IOptionsMonitor<ServerOptions> options,
    IOptionsFactory<ServerOptions> factory,
    ILogger<WordsModel> log) : PageModel
{
    // ponytail: each term is a regex pass over every chat line; raise if a host needs more.
    public const int MaxTerms = 200;

    public bool FromPortal => live.BlockedTerms is not null;
    public string Text { get; private set; } = "";

    [TempData] public string? Message { get; set; }
    public IReadOnlyList<string> Errors { get; private set; } = [];

    public void OnGet() => Text = string.Join('\n', options.CurrentValue.Protocol.BlockedTerms);

    public async Task<IActionResult> OnPostSaveAsync(string? terms, CancellationToken ct)
    {
        var list = (terms ?? "").Split('\n')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var errors = list.Count > MaxTerms
            ? [$"At most {MaxTerms} terms; this list has {list.Count}."]
            : await live.SaveBlockedTermsAsync(db, factory, list, ct);
        if (errors.Count > 0)
        {
            Errors = errors;
            Text = terms ?? "";
            return Page();
        }

        log.LogWarning("Admin {Admin} saved {Count} blocked terms", User.Identity?.Name, list.Count);
        Message = $"Saved {list.Count} term{(list.Count == 1 ? "" : "s")}; in effect now.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetAsync(CancellationToken ct)
    {
        await live.ResetBlockedTermsAsync(db, ct);
        log.LogWarning("Admin {Admin} reset the blocked terms to server.yaml", User.Identity?.Name);
        Message = "Back to server.yaml's list.";
        return RedirectToPage();
    }
}
