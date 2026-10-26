// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Pages.Admin;

/// <summary>
/// The lobby list. Lobby ids are positions, so the list is saved whole and replaces
/// server.yaml's at the next start (<see cref="LiveSettings.Lobbies"/>).
/// </summary>
public sealed class LobbiesModel(
    GameDbContext db,
    LiveSettings live,
    LobbyCatalog catalog,
    Hub hub,
    IOptionsFactory<ServerOptions> factory,
    ILogger<LobbiesModel> log) : PageModel
{
    public const int MaxName = 32;
    public const int MaxPlayersLimit = 1000;

    public IReadOnlyList<Lobby> Running => catalog.Lobbies;
    public int Players(Lobby lobby) => hub.SessionsInBlock(lobby.Id).Count;

    /// <summary>Running from a saved list rather than server.yaml.</summary>
    public bool FromPortal => live.Lobbies is not null;

    /// <summary>What the form shows: the saved list if any, else what runs now; padded to the slot count.</summary>
    public List<LobbyOptions> Slots { get; private set; } = [];

    /// <summary>A saved list (or a reset) that the running server has not picked up yet.</summary>
    public bool RestartPending { get; private set; }

    [TempData] public string? Message { get; set; }
    public IReadOnlyList<string> Errors { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(null, ct);

    public async Task<IActionResult> OnPostSaveAsync(string?[] name, int[] max, CancellationToken ct)
    {
        // The form posts every slot in order; name[i] and max[i] belong together.
        var lobbies = name
            .Select((n, i) => new LobbyOptions
            {
                Name = (n ?? "").Trim()[..Math.Min((n ?? "").Trim().Length, MaxName)],
                MaxPlayers = Math.Clamp(i < max.Length ? max[i] : 0, 0, MaxPlayersLimit),
            })
            .Where(l => l.Name.Length > 0)
            .Take(ServerOptions.MaxLobbies)
            .ToList();

        if (await live.SaveLobbiesAsync(db, factory, lobbies, ct) is { Count: > 0 } failures)
        {
            Errors = failures;
            await LoadAsync(lobbies, ct);
            return Page();
        }

        log.LogWarning("Admin {Admin} saved the lobby list: {Lobbies}", User.Identity?.Name, string.Join(", ", lobbies.Select(l => l.Name)));
        Message = "Lobby list saved. Restart the server to apply it.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetAsync(CancellationToken ct)
    {
        await LiveSettings.ResetLobbiesAsync(db, ct);
        log.LogWarning("Admin {Admin} reset the lobby list to server.yaml", User.Identity?.Name);
        Message = "Saved list removed. server.yaml's lobbies apply after the next restart.";
        return RedirectToPage();
    }

    private async Task LoadAsync(List<LobbyOptions>? posted, CancellationToken ct)
    {
        var saved = await LiveSettings.SavedLobbiesAsync(db, ct);
        var running = Running.Select(l => new LobbyOptions { Name = l.Name, MaxPlayers = l.MaxPlayers }).ToList();
        RestartPending = saved is null ? FromPortal : !saved.Select(l => (l.Name, l.MaxPlayers)).SequenceEqual(running.Select(l => (l.Name, l.MaxPlayers)));

        Slots = posted ?? saved ?? running;
        while (Slots.Count < ServerOptions.MaxLobbies)
            Slots.Add(new LobbyOptions { Name = "", MaxPlayers = 100 });
    }
}
