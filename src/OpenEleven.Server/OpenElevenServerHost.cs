// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.Logging;
using OpenEleven.Server.State;
using OpenEleven.Server.Transport;
using OpenEleven.Server.Web;

namespace OpenEleven.Server;

/// <summary>
/// The shared bootstrap every per-title executable runs. Each title has a slim exe whose
/// Program.cs is a single call to <see cref="Run"/> with its <see cref="GameProfile"/>;
/// everything else (configuration, transport, dispatch, web) is identical and lives here.
/// </summary>
public static class OpenElevenServerHost
{
    /// <summary>
    /// Builds and runs the server for one title. One process = one title: the profile
    /// selects which profile-gated commands register and which profile assembly is scanned.
    /// </summary>
    public static async Task<int> Run(GameProfile profile, string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // wwwroot lives in this shared library, not in the per-title exe, so Static Web
        // Assets must be loaded explicitly: ASP.NET Core only auto-loads them when
        // ASPNETCORE_ENVIRONMENT=Development, which the title exes don't set.
        builder.WebHost.UseStaticWebAssets();

        // Configuration: YAML file, an optional local override, then OPENELEVEN_-prefixed
        // environment variables (OPENELEVEN_Server__Database__ConnectionString and friends).
        var configPath = GetConfigPath(args)
            ?? Path.Combine(AppContext.BaseDirectory, "conf", "server.yaml");

        builder.Configuration
            .AddYamlFile(configPath, optional: false, reloadOnChange: true)
            .AddYamlFile(Path.ChangeExtension(configPath, ".local.yaml"), optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("OPENELEVEN_");

        // The profile comes from the exe (code), not config: it is fixed per executable and
        // choosing it in YAML would let one binary claim to be a title it has no code for.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{ServerOptions.SectionName}:{nameof(ServerOptions.GameProfile)}"] = profile.ToString(),
        });

        builder.Services.AddOpenElevenCore(builder.Configuration);
        builder.Services.AddHostedService<RoomWatchPublisher>();

        // Serves the registration form at /register. Razor Pages ships in the shared
        // framework, so this needs no package reference.
        builder.Services.AddRazorPages();
        builder.Services.AddHttpRateLimits();

        // The public pages read whole tables; a few seconds of staleness keeps that off the database.
        builder.Services.AddOutputCache();

        // Antiforgery tokens are protected with Data Protection keys, which default to a
        // per-user directory that a container does not persist. Without this, every restart
        // invalidates the tokens on already-open registration pages and the next submit fails.
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(
                Path.Combine(AppContext.BaseDirectory, "data", "keys")));

        var startupOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>()
                             ?? new ServerOptions();

        // Durable copy of the log. Console output is gone as soon as it scrolls, and a client
        // attempt that fails in two seconds is exactly the evidence that cannot be re-derived.
        FileLoggerProvider? fileLog = null;
        if (!string.IsNullOrWhiteSpace(startupOptions.LogFile))
        {
            var logPath = Path.GetFullPath(
                Path.IsPathRooted(startupOptions.LogFile)
                    ? startupOptions.LogFile
                    : Path.Combine(AppContext.BaseDirectory, startupOptions.LogFile));

            fileLog = new FileLoggerProvider(logPath);
            builder.Logging.AddProvider(fileLog);
        }

        // One TCP listener per logical service. Only the gate port is fixed, because the
        // client hardcodes it; every other port is discovered from the server list.
        foreach (var endpoint in startupOptions.Services.Where(s => s.Enabled))
        {
            builder.Services.AddSingleton<IHostedService>(sp => new GameListener(
                endpoint,
                sp.GetRequiredService<Hub>(),
                sp.GetRequiredService<ProtocolCodecs>(),
                sp.GetRequiredService<CommandDispatcher>(),
                sp.GetRequiredService<IOptionsMonitor<ServerOptions>>(),
                sp.GetRequiredService<ILoggerFactory>()));
        }

        // Kestrel serves the small HTTP surface the client fetches before logging in: HTTPS, or
        // plain HTTP on 80 when Https.Enabled is false (local development). With HTTPS there is
        // no fallback: without a certificate the client cannot get past CMD_GET_URLLIST, so a
        // missing one stops the server here instead.
        var certificate = startupOptions.Https.Enabled ? LoadCertificate(startupOptions.Https) : null;
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (certificate is null)
            {
                kestrel.ListenAnyIP(startupOptions.Listen.Http, listen => listen.Protocols = HttpProtocols.Http1);
                return;
            }

            kestrel.ListenAnyIP(startupOptions.Listen.Https, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate);
            });
        });

        var app = builder.Build();

        await InitializeDatabaseAsync(app);
        LogStartupSummary(app, startupOptions, fileLog);

        // The ranked list of commands the client wanted and did not get is the
        // reverse-engineering worklist, printed once on the way out.
        app.Lifetime.ApplicationStopping.Register(
            () => app.Services.GetRequiredService<UnknownCommandLog>().Report());

        // Static files first, so stylesheet requests short-circuit before the hex-dump trace.
        app.UseStaticFiles();

        // Explicit, not implicit. HttpTraceMiddleware decides whether to redact a body by
        // reading the matched endpoint's metadata, so routing has to have run by then.
        app.UseRouting();

        app.UseMiddleware<HttpTraceMiddleware>();
        app.UseRateLimiter();
        app.UseOutputCache();

        app.MapRazorPages();
        app.MapGameEndpoints();

        await app.RunAsync();
        return 0;
    }

    private static string? GetConfigPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] is "--config" or "-c")
                return Path.GetFullPath(args[i + 1]);
        return null;
    }

    private static async Task InitializeDatabaseAsync(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<ServerOptions>>().CurrentValue;
        if (!options.Database.AutoCreate)
            return;

        await using var scope = app.Services.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync();
    }

    /// <summary>Loads the PEM chain + key named in <see cref="HttpsOptions"/>.</summary>
    internal static X509Certificate2 LoadCertificate(HttpsOptions https)
    {
        var certPath = Path.GetFullPath(https.CertificatePath, AppContext.BaseDirectory);
        var keyPath = Path.GetFullPath(https.KeyPath, AppContext.BaseDirectory);
        if (https.CertificatePath.Length == 0 || !File.Exists(certPath))
            throw new InvalidOperationException($"Https.CertificatePath: certificate '{certPath}' not found.");
        if (https.KeyPath.Length == 0 || !File.Exists(keyPath))
            throw new InvalidOperationException($"Https.KeyPath: private key '{keyPath}' not found.");

        using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        // SChannel on Windows refuses the ephemeral key a PEM import produces; a PKCS#12
        // round trip gives it a persisted one.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
    }

    private static void LogStartupSummary(
        WebApplication app, ServerOptions options, FileLoggerProvider? fileLog)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OpenEleven.Startup");
        var catalog = app.Services.GetRequiredService<ServerCatalog>();
        var registry = app.Services.GetRequiredService<CommandRegistry>();

        log.LogInformation(
            "Serving {Title}: advertising {Host}", TitleProfiles.DisplayName(options.GameProfile), catalog.AdvertiseIp);
        log.LogInformation("{Count} commands registered", registry.Count);
        if (options.Https.Enabled)
            log.LogInformation("HTTPS: https://{Host}:{Port}/", catalog.AdvertiseIp, options.Listen.Https);
        else
            log.LogWarning("HTTPS disabled: plain http://{Host}:{Port}/ (local development only)", catalog.AdvertiseIp, options.Listen.Http);

        if (fileLog is { IsOpen: true })
            log.LogInformation("Log file: {LogFile}", fileLog.Path);
        else if (!string.IsNullOrWhiteSpace(options.LogFile))
            log.LogWarning("Log file {LogFile} could not be opened; console logging only", options.LogFile);

        foreach (var service in options.Services.Where(s => s.Enabled))
            log.LogInformation(
                "  {Role,-8} gid={Gid} port={Port} advertise={Advertise}",
                service.Role, service.Gid, service.Port, service.Advertise);
    }
}
