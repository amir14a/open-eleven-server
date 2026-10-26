// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Entities;

namespace OpenEleven.Server.Configuration;

/// <summary>
/// Settings changed from the admin portal. Stored in the ServerSettings table under
/// <see cref="Prefix"/> and layered over server.yaml as the last configuration source, so every
/// <see cref="IOptionsMonitor{TOptions}"/> reader sees a change without a restart.
/// <para>
/// The lobby list is the exception: a configuration layer can change entries but never remove
/// one, and lobby ids are positions, so a list saved under <see cref="LobbyPrefix"/> replaces
/// server.yaml's whole list (<see cref="ReplaceLobbies"/>) from the next start.
/// </para>
/// </summary>
public sealed class LiveSettings : ConfigurationProvider, IConfigurationSource
{
    public const string Prefix = "config:";
    public const string LobbyPrefix = "lobbies:";

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    // A set under validation, seen only by the flow running Check: other threads reading
    // configuration meanwhile (a server.yaml reload, a second save) keep seeing Data.
    private readonly AsyncLocal<IDictionary<string, string?>?> _candidate = new();
    private readonly AsyncLocal<IReadOnlyList<LobbyOptions>?> _candidateLobbies = new();

    /// <summary>The saved lobby list in force since startup; null while server.yaml's applies.</summary>
    public IReadOnlyList<LobbyOptions>? Lobbies { get; private set; }

    /// <summary>Post-configure step: puts the saved lobby list (or one under validation) in place of server.yaml's.</summary>
    public void ReplaceLobbies(ServerOptions options)
    {
        if ((_candidateLobbies.Value ?? Lobbies) is { } lobbies)
            options.Lobbies = lobbies.Select(l => new LobbyOptions { Name = l.Name, MaxPlayers = l.MaxPlayers }).ToList();
    }

    private IDictionary<string, string?> View => _candidate.Value ?? Data;

    public IReadOnlyDictionary<string, string?> Overrides => new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);

    public override bool TryGet(string key, out string? value) => View.TryGetValue(key, out value);

    public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
    {
        var prefix = parentPath is null ? "" : parentPath + ConfigurationPath.KeyDelimiter;
        return View.Keys
            .Where(k => k.Length > prefix.Length && k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split(ConfigurationPath.KeyDelimiter, 2)[0])
            .Concat(earlierKeys)
            .Order(ConfigurationKeyComparer.Instance);
    }

    /// <summary>
    /// The validation failures <paramref name="candidate"/> would cause, empty when it is safe.
    /// An invalid set must never go live: every <c>CurrentValue</c> would throw from then on.
    /// </summary>
    public IReadOnlyList<string> Check(IReadOnlyDictionary<string, string?> candidate, IOptionsFactory<ServerOptions> factory)
    {
        if (Unlisted(candidate.Keys, factory) is { Count: > 0 } unlisted)
            return unlisted.Select(k => $"{k} cannot be changed from the admin portal.").ToList();
        return Validate(candidate, factory);
    }

    private IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string?> candidate, IOptionsFactory<ServerOptions> factory)
    {
        // The factory binds straight from configuration, which reads this provider on every
        // lookup; the monitor's cached options are untouched until OnReload.
        _candidate.Value = new Dictionary<string, string?>(candidate, StringComparer.OrdinalIgnoreCase);
        try
        {
            factory.Create(Options.DefaultName);
            return [];
        }
        catch (OptionsValidationException ex)
        {
            return ex.Failures.ToList();
        }
        catch (InvalidOperationException ex)   // a value the binder cannot convert
        {
            return [ex.InnerException?.Message ?? ex.Message];
        }
        finally
        {
            _candidate.Value = null;
        }
    }

    /// <summary>
    /// Keys outside <see cref="LiveSettingCatalog"/>. Only the catalog may be layered over
    /// server.yaml: a row written straight into the database must not reach Admin, Captcha,
    /// Https or anything else the portal does not offer.
    /// </summary>
    private static List<string> Unlisted(IEnumerable<string> keys, IOptionsFactory<ServerOptions> factory)
    {
        var options = factory.Create(Options.DefaultName);
        var listed = LiveSettingCatalog.For(options, options.GameProfile).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return keys.Where(k => !listed.Contains(k)).ToList();
    }

    public void Apply(IReadOnlyDictionary<string, string?> values)
    {
        Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }

    /// <summary>Applies what the database holds, unless the current server.yaml makes it invalid.</summary>
    public async Task LoadAsync(GameDbContext db, IOptionsFactory<ServerOptions> factory, ILogger log, CancellationToken ct = default)
    {
        if (await SavedLobbiesAsync(db, ct) is { } lobbies)
        {
            if (CheckLobbies(lobbies, factory) is { Count: > 0 } invalid)
                log.LogError("Ignoring the lobby list saved from the admin portal: {Failures}", string.Join(" ", invalid));
            else
            {
                Lobbies = lobbies;
                OnReload();   // options read before this point were bound with server.yaml's list
                log.LogInformation("Using the {Count} lobbies saved from the admin portal", lobbies.Count);
            }
        }

        var stored = await db.Settings.Where(s => s.Key.StartsWith(Prefix)).ToListAsync(ct);
        if (stored.Count == 0)
            return;

        var values = stored.ToDictionary(s => s.Key[Prefix.Length..], s => (string?)s.Value, StringComparer.OrdinalIgnoreCase);
        if (Unlisted(values.Keys, factory) is { Count: > 0 } unlisted)
        {
            log.LogWarning("Ignoring saved settings the admin portal does not offer: {Keys}", string.Join(", ", unlisted));
            unlisted.ForEach(k => values.Remove(k));
        }

        if (values.Count == 0)
            return;
        if (Check(values, factory) is { Count: > 0 } failures)
        {
            log.LogError("Ignoring the settings saved from the admin portal: {Failures}", string.Join(" ", failures));
            return;
        }

        Apply(values);
        log.LogInformation("Applied {Count} settings saved from the admin portal", values.Count);
    }

    /// <summary>Validates, stores and applies <paramref name="values"/> as the full set of overrides.</summary>
    public async Task<IReadOnlyList<string>> SaveAsync(GameDbContext db, IOptionsFactory<ServerOptions> factory,
        IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        if (Check(values, factory) is { Count: > 0 } failures)
            return failures;

        var stored = await db.Settings.Where(s => s.Key.StartsWith(Prefix)).ToListAsync(ct);
        db.Settings.RemoveRange(stored.Where(s => !values.ContainsKey(s.Key[Prefix.Length..])));
        foreach (var (key, value) in values)
        {
            if (stored.FirstOrDefault(s => s.Key == Prefix + key) is { } row)
                row.Value = value ?? "";
            else
                db.Settings.Add(new ServerSetting { Key = Prefix + key, Value = value ?? "" });
        }

        await db.SaveChangesAsync(ct);
        Apply(values);
        return [];
    }

    /// <summary>The lobby list saved for the next start, or null when none is (server.yaml's then applies).</summary>
    public static async Task<List<LobbyOptions>?> SavedLobbiesAsync(GameDbContext db, CancellationToken ct = default)
    {
        // One row per lobby, "lobbies:00".."lobbies:09" in order, valued "<max players>|<name>".
        var rows = await db.Settings.Where(s => s.Key.StartsWith(LobbyPrefix)).ToListAsync(ct);
        if (rows.Count == 0)
            return null;

        return rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r =>
        {
            var parts = r.Value.Split('|', 2);
            return new LobbyOptions { MaxPlayers = int.TryParse(parts[0], out var max) ? max : 0, Name = parts.ElementAtOrDefault(1) ?? "" };
        }).ToList();
    }

    /// <summary>Validates <paramref name="lobbies"/> with everything else as it runs now, then stores it for the next start.</summary>
    public async Task<IReadOnlyList<string>> SaveLobbiesAsync(GameDbContext db, IOptionsFactory<ServerOptions> factory,
        IReadOnlyList<LobbyOptions> lobbies, CancellationToken ct = default)
    {
        if (lobbies.Count == 0)
            return ["Keep at least one lobby."];
        if (CheckLobbies(lobbies, factory) is { Count: > 0 } failures)
            return failures;

        db.Settings.RemoveRange(await db.Settings.Where(s => s.Key.StartsWith(LobbyPrefix)).ToListAsync(ct));
        for (var i = 0; i < lobbies.Count; i++)
            db.Settings.Add(new ServerSetting { Key = $"{LobbyPrefix}{i:D2}", Value = $"{lobbies[i].MaxPlayers}|{lobbies[i].Name}" });
        await db.SaveChangesAsync(ct);
        return [];
    }

    /// <summary>Forgets the saved lobby list, so server.yaml's applies from the next start.</summary>
    public static async Task ResetLobbiesAsync(GameDbContext db, CancellationToken ct = default)
    {
        db.Settings.RemoveRange(await db.Settings.Where(s => s.Key.StartsWith(LobbyPrefix)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    private IReadOnlyList<string> CheckLobbies(IReadOnlyList<LobbyOptions> lobbies, IOptionsFactory<ServerOptions> factory)
    {
        _candidateLobbies.Value = lobbies;
        try
        {
            return Validate(Overrides, factory);
        }
        finally
        {
            _candidateLobbies.Value = null;
        }
    }
}
