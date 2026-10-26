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
/// </summary>
public sealed class LiveSettings : ConfigurationProvider, IConfigurationSource
{
    public const string Prefix = "config:";

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public IReadOnlyDictionary<string, string?> Overrides => new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The validation failures <paramref name="candidate"/> would cause, empty when it is safe.
    /// An invalid set must never go live: every <c>CurrentValue</c> would throw from then on.
    /// </summary>
    public IReadOnlyList<string> Check(IReadOnlyDictionary<string, string?> candidate, IOptionsFactory<ServerOptions> factory)
    {
        // The factory binds straight from configuration, which reads this provider on every
        // lookup; the monitor's cached options are untouched until OnReload.
        var current = Data;
        Data = new Dictionary<string, string?>(candidate, StringComparer.OrdinalIgnoreCase);
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
            Data = current;
        }
    }

    public void Apply(IReadOnlyDictionary<string, string?> values)
    {
        Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }

    /// <summary>Applies what the database holds, unless the current server.yaml makes it invalid.</summary>
    public async Task LoadAsync(GameDbContext db, IOptionsFactory<ServerOptions> factory, ILogger log, CancellationToken ct = default)
    {
        var stored = await db.Settings.Where(s => s.Key.StartsWith(Prefix)).ToListAsync(ct);
        if (stored.Count == 0)
            return;

        var values = stored.ToDictionary(s => s.Key[Prefix.Length..], s => (string?)s.Value, StringComparer.OrdinalIgnoreCase);
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
}
