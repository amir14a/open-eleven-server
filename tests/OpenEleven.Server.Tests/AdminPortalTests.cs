// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Tests;

public class AdminPortalTests
{
    /// <summary>server.yaml stand-in with <paramref name="live"/> layered last, as the host does.</summary>
    private static (IOptionsMonitor<ServerOptions> Monitor, IOptionsFactory<ServerOptions> Factory) Options(LiveSettings live)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:AdvertiseIp"] = "oe.test",
                ["Server:Lobbies:0:Name"] = "Beginner",
                ["Server:Lobbies:1:Name"] = "Expert",
            })
            .Add(live)
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<ServerOptions>().Bind(config.GetSection(ServerOptions.SectionName));
        services.AddSingleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IOptionsMonitor<ServerOptions>>(), provider.GetRequiredService<IOptionsFactory<ServerOptions>>());
    }

    [Fact]
    public void A_change_reaches_the_running_options_without_a_restart()
    {
        var live = new LiveSettings();
        var (monitor, _) = Options(live);
        Assert.Equal("Expert", monitor.CurrentValue.Lobbies[1].Name);

        live.Apply(new Dictionary<string, string?> { ["Server:Lobbies:1:Name"] = "Pro", ["Server:Rank:WinPoints"] = "4" });

        Assert.Equal(("Pro", 4), (monitor.CurrentValue.Lobbies[1].Name, monitor.CurrentValue.Rank.WinPoints));
    }

    [Fact]
    public void An_invalid_change_is_refused_and_nothing_moves()
    {
        var live = new LiveSettings();
        var (monitor, factory) = Options(live);

        var failures = live.Check(new Dictionary<string, string?> { ["Server:Lobbies:1:Name"] = "Beginner" }, factory);

        Assert.Contains(failures, f => f.Contains("unique"));
        Assert.Empty(live.Overrides);
        Assert.Equal("Expert", monitor.CurrentValue.Lobbies[1].Name);
    }

    [Fact]
    public async Task Only_catalog_settings_are_accepted_or_loaded()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var live = new LiveSettings();
        var (monitor, factory) = Options(live);

        Assert.NotEmpty(live.Check(new Dictionary<string, string?> { ["Server:Admin:Password"] = "a-long-secret-1" }, factory));

        // A row written straight into the database, next to a legitimate one.
        db.Settings.Add(new() { Key = LiveSettings.Prefix + "Server:Admin:Password", Value = "a-long-secret-1" });
        db.Settings.Add(new() { Key = LiveSettings.Prefix + "Server:Site:Announcement", Value = "Cup final" });
        await db.SaveChangesAsync();
        await live.LoadAsync(db, factory, NullLogger.Instance);

        Assert.Equal(("", "Cup final"), (monitor.CurrentValue.Admin.Password, monitor.CurrentValue.Site.Announcement));
    }

    [Fact]
    public async Task Saved_settings_survive_a_restart_and_a_reset_removes_them()
    {
        await using var harness = await ServerHarness.CreateAsync();
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var live = new LiveSettings();
        var (_, factory) = Options(live);

        Assert.Empty(await live.SaveAsync(db, factory, new Dictionary<string, string?> { ["Server:Site:Announcement"] = "Cup final" }));

        var restarted = new LiveSettings();
        var (monitor, restartedFactory) = Options(restarted);
        await restarted.LoadAsync(db, restartedFactory, NullLogger.Instance);
        Assert.Equal("Cup final", monitor.CurrentValue.Site.Announcement);

        await restarted.SaveAsync(db, restartedFactory, new Dictionary<string, string?>());
        Assert.Equal("", monitor.CurrentValue.Site.Announcement);
        Assert.DoesNotContain(db.Settings, s => s.Key.StartsWith(LiveSettings.Prefix));
    }

    [Fact]
    public async Task Kicking_an_account_closes_all_its_connections_and_only_those()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var (lobby, menu) = (harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated), harness.NewSession(ServiceRole.Menu, SessionState.Authenticated));
        var other = harness.NewSession(ServiceRole.Menu, SessionState.Authenticated);
        other.AccountId = lobby.AccountId + 1;

        Assert.Equal(2, harness.Hub.Disconnect(lobby.AccountId));

        Assert.True(lobby.Queue.Reader.Completion.IsCompleted && menu.Queue.Reader.Completion.IsCompleted);
        Assert.False(other.Queue.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void Only_the_configured_user_and_password_sign_in()
    {
        var admin = new AdminOptions { User = "admin", Password = "a-long-secret-1" };

        Assert.True(AdminAuth.Matches(admin, "admin", "a-long-secret-1"));
        Assert.False(AdminAuth.Matches(admin, "admin", "a-long-secret-2"));
        Assert.False(AdminAuth.Matches(admin, "root", "a-long-secret-1"));
        Assert.False(AdminAuth.Matches(new AdminOptions { Password = "" }, "admin", ""));   // portal off
    }

    [Fact]
    public void A_short_admin_password_stops_the_server()
    {
        var options = new ServerOptions { AdvertiseIp = "oe.test", Admin = { Password = "short" } };
        Assert.Contains(new ServerOptionsValidator().Validate(null, options).Failures ?? [], f => f.StartsWith("Admin"));
    }
}
