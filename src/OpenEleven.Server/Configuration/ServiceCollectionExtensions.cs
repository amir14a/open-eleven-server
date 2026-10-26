// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Protocol.Framing;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;
using OpenEleven.Server.Transport;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Configuration;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything that is not a transport: protocol codecs, global state,
    /// dispatch and data access. Shared by the host and by the tests so they cannot
    /// drift apart.
    /// </summary>
    public static IServiceCollection AddOpenElevenCore(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<ServerOptions>()
            .Bind(configuration.GetSection(ServerOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddSingleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>();

        // A relative SQLite path must mean the same file however the server was started,
        // so it is anchored before anything reads the connection string.
        services.PostConfigure<ServerOptions>(o =>
        {
            if (o.Database.Provider == DatabaseProvider.Sqlite)
                o.Database.ConnectionString = DatabasePath.ResolveSqlite(o.Database.ConnectionString);

            // Plain variable names so a docker .env can pass them through even when unset:
            // an empty one leaves the YAML value alone.
            static void FromEnv(string name, Action<string> set)
            {
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                    set(value);
            }

            FromEnv("CAPTCHA_PROVIDER", v => o.Captcha.Provider = v);
            FromEnv("CAPTCHA_SITE_KEY", v => o.Captcha.SiteKey = v);
            FromEnv("CAPTCHA_SECRET_KEY", v => o.Captcha.SecretKey = v);
            FromEnv("ADMIN_USER", v => o.Admin.User = v);
            FromEnv("ADMIN_PASSWORD", v => o.Admin.Password = v);
        });

        var options = configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>()
                      ?? new ServerOptions();

        if (options.Database.Provider == DatabaseProvider.Sqlite)
            options.Database.ConnectionString =
                DatabasePath.ResolveSqlite(options.Database.ConnectionString);

        // The active title, registered so dispatch and future per-title strategies can
        // take it as a direct dependency instead of re-reading options. GameProfile is a
        // value type, so this uses the non-generic overload (the generic one is
        // class-constrained); GetRequiredService<GameProfile>() resolves it fine.
        services.AddSingleton(typeof(GameProfile), options.GameProfile);

        // --- singletons: stateless codecs and the one global state store ---
        // The cipher keys are shared by every supported title, so they come from
        // GameCrypto constants rather than per-title configuration.
        services.AddSingleton(new XorCipher(GameCrypto.XorKey));
        services.AddSingleton(new BlowfishEcb(GameCrypto.BlowfishKey));
        services.AddSingleton<PacketCodec>();
        services.AddSingleton<KvReader>();
        services.AddSingleton<KvWriter>();
        services.AddSingleton<ProtocolCodecs>();

        services.AddSingleton<MatchResults>();
        services.AddHostedService<RankScheduler>();
        services.AddSingleton<Hub>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PendingLoginStore>();
        services.AddSingleton<GameIdTicketStore>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<ServerCatalog>();
        services.AddSingleton<LobbyCatalog>();
        services.AddSingleton<ProtocolTextPolicy>();
        services.AddSingleton<ChatTextPolicy>();
        services.AddSingleton<WebAssets>();
        services.AddScoped<GameIdAuthService>();
        services.AddScoped<RegistrationService>();
        services.AddHttpClient<CaptchaVerifier>(http => http.Timeout = TimeSpan.FromSeconds(10));

        // The core assembly always supplies the shared commands. The configured title's
        // profile assembly (when the host references it) adds or overrides with its
        // profile-gated commands; it is filtered by the active profile inside Build.
        var scanAssemblies = new List<System.Reflection.Assembly>
        {
            typeof(ServiceCollectionExtensions).Assembly,
        };
        var profileAssembly = TitleProfiles.TryLoadAssembly(options.GameProfile);
        if (profileAssembly is not null && profileAssembly != scanAssemblies[0])
            scanAssemblies.Add(profileAssembly);

        services.AddSingleton(_ => CommandRegistry.Build(
            options.GameProfile, scanAssemblies.ToArray()));
        services.AddSingleton<UnknownCommandLog>();
        services.AddSingleton<CommandDispatcher>();

        // --- scoped: one DI scope per dispatched command ---
        foreach (var handlerType in CommandRegistry.DiscoverHandlerTypes(
                     options.GameProfile, scanAssemblies.ToArray()))
            services.AddScoped(handlerType);

        services.AddDbContext<GameDbContext>(dbOptions =>
        {
            var db = options.Database;
            switch (db.Provider)
            {
                case DatabaseProvider.MySql:
                    dbOptions.UseMySql(db.ConnectionString, ServerVersion.AutoDetect(db.ConnectionString));
                    break;
                default:
                    dbOptions.UseSqlite(db.ConnectionString);
                    break;
            }
        });

        services.AddScoped<IPlayerRepository, PlayerRepository>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<ICatalogRepository, CatalogRepository>();
        services.AddScoped<IMatchRepository, MatchRepository>();
        services.AddScoped<DatabaseInitializer>();

        return services;
    }
}
