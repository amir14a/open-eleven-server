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

/// <summary>Live settings (<see cref="LiveSettingCatalog"/>), saved over server.yaml and applied at once.</summary>
public sealed class SettingsModel(
    GameDbContext db,
    LiveSettings live,
    IOptionsMonitor<ServerOptions> options,
    IOptionsFactory<ServerOptions> factory,
    GameProfile game,
    ILogger<SettingsModel> log) : PageModel
{
    public ServerOptions Current => options.CurrentValue;
    public IReadOnlyList<LiveSetting> Settings => LiveSettingCatalog.For(Current, game);
    public IReadOnlyDictionary<string, string?> Overrides => live.Overrides;

    [TempData] public string? Message { get; set; }
    public IReadOnlyList<string> Errors { get; private set; } = [];

    public void OnGet() { }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var values = new Dictionary<string, string?>(live.Overrides, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var changed = new List<string>();
        foreach (var setting in Settings)
        {
            if (setting.Normalize(Request.Form[setting.Key]) is not { } value)
                errors.Add($"{setting.Group} · {setting.Label}: not a valid value.");
            else if (value != setting.Current(Current))
            {
                values[setting.Key] = value;
                changed.Add(setting.Key);
            }
        }

        if (errors.Count == 0 && changed.Count > 0)
            errors.AddRange(await live.SaveAsync(db, factory, values, ct));
        if (errors.Count > 0)
        {
            Errors = errors;
            return Page();
        }

        if (changed.Count > 0)
            log.LogWarning("Admin {Admin} changed settings: {Keys}", User.Identity?.Name, string.Join(", ", changed));
        Message = changed.Count == 0 ? "Nothing changed." : $"Saved {changed.Count} setting{(changed.Count == 1 ? "" : "s")}; in effect now.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetAsync(string key, CancellationToken ct)
    {
        var values = new Dictionary<string, string?>(live.Overrides, StringComparer.OrdinalIgnoreCase);
        if (values.Remove(key))
        {
            if (await live.SaveAsync(db, factory, values, ct) is { Count: > 0 } failures)
            {
                Errors = failures;
                return Page();
            }

            log.LogWarning("Admin {Admin} reset {Key} to the server.yaml value", User.Identity?.Name, key);
            Message = "Back to the server.yaml value.";
        }

        return RedirectToPage();
    }
}
