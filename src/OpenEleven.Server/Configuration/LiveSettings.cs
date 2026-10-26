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

    // A set under validation, seen only by the flow running Check: other threads reading
    // configuration meanwhile (a server.yaml reload, a second save) keep seeing Data.
    private readonly AsyncLocal<IDictionary<string, string?>?> _candidate = new();

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
        var stored = await db.Settings.Where(s => s.Key.StartsWith(Prefix)).ToListAsync(ct);
        if (stored.Count == 0)
            return;

        var values = stored.ToDictionary(s => s.Key[Prefix.Length..], s => (string?)s.Value, StringComparer.OrdinalIgnoreCase);
        // Also drops a lobby that server.yaml no longer has; the next save deletes its rows.
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
}
