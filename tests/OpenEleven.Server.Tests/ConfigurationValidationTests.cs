// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class ServerOptionsValidatorTests
{
    private readonly ServerOptionsValidator _validator = new();

    private static ServerOptions WithLobbies(int count)
    {
        var options = new ServerOptions { AdvertiseIp = "oe.test" };
        for (var i = 0; i < count; i++)
            options.Lobbies.Add(new LobbyOptions { Name = $"Lobby {i}" });
        return options;
    }

    [Fact]
    public void Accepts_the_maximum_number_of_lobbies()
        => Assert.False(_validator
            .Validate(null, WithLobbies(ServerOptions.MaxLobbies)).Failed);

    [Fact]
    public void Rejects_more_lobbies_than_the_maximum()
    {
        var result = _validator.Validate(null, WithLobbies(ServerOptions.MaxLobbies + 1));

        Assert.True(result.Failed);
        Assert.Contains("At most 10 lobbies", string.Join(' ', result.Failures!));
    }

    [Fact]
    public void Disabled_lobbies_do_not_count_towards_the_limit()
    {
        var options = WithLobbies(ServerOptions.MaxLobbies + 2);
        options.Lobbies[0].Enabled = false;
        options.Lobbies[1].Enabled = false;

        Assert.False(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Rejects_a_lobby_without_a_name()
    {
        var options = WithLobbies(2);
        options.Lobbies[1].Name = "   ";

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Rejects_duplicate_lobby_names()
    {
        var options = WithLobbies(2);
        options.Lobbies[1].Name = options.Lobbies[0].Name;

        Assert.Contains("names must be unique",
            string.Join(' ', _validator.Validate(null, options).Failures!));
    }

    [Fact]
    public void Rejects_duplicate_lobby_ids()
    {
        var options = WithLobbies(2);
        options.Lobbies[0].Id = 5;
        options.Lobbies[1].Id = 5;

        Assert.Contains("ids must be unique",
            string.Join(' ', _validator.Validate(null, options).Failures!));
    }

    [Fact]
    public void Rejects_two_services_sharing_a_port()
    {
        var options = new ServerOptions { AdvertiseIp = "oe.test" };
        options.Services.Add(new ServiceEndpointOptions
        {
            Role = ServiceRole.Gate, Name = "Gate", Gid = 1, Port = 28010,
        });
        options.Services.Add(new ServiceEndpointOptions
        {
            Role = ServiceRole.Lobby, Name = "Lobby", Gid = 3, Port = 28010,
        });

        Assert.Contains("cannot share a port",
            string.Join(' ', _validator.Validate(null, options).Failures!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ServerOptions.MaxLobbies * 100)]
    public void Rejects_player_search_limits_outside_the_supported_range(int limit)
    {
        var options = new ServerOptions { AdvertiseIp = "oe.test" };
        options.Protocol.PlayerSearchLimit = limit;

        Assert.Contains("PlayerSearchLimit",
            string.Join(' ', _validator.Validate(null, options).Failures!));
    }

    [Fact]
    public void Rejects_invalid_blocked_terms()
    {
        var options = new ServerOptions { AdvertiseIp = "oe.test" };
        options.Protocol.BlockedTerms.Add("bad\u0001word");

        Assert.Contains("BlockedTerms",
            string.Join(' ', _validator.Validate(null, options).Failures!));
    }
}

public class LobbyCapEnforcementTests
{
    /// <summary>Feeds the catalog directly, bypassing the startup validation.</summary>
    private sealed class StaticMonitor(ServerOptions value)
        : Microsoft.Extensions.Options.IOptionsMonitor<ServerOptions>
    {
        public ServerOptions CurrentValue { get; } = value;
        public ServerOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ServerOptions, string?> listener) => null;
    }

    [Fact]
    public void The_catalog_never_serves_more_than_the_maximum()
    {
        // Belt and braces: startup validation rejects an over-long list, but a hot reload
        // could introduce one, and serving eleven blocks is worse than serving ten.
        var options = new ServerOptions { AdvertiseIp = "oe.test" };
        for (var i = 0; i < ServerOptions.MaxLobbies + 5; i++)
            options.Lobbies.Add(new LobbyOptions { Name = $"Lobby {i}" });

        var catalog = new LobbyCatalog(new StaticMonitor(options));

        Assert.Equal(ServerOptions.MaxLobbies, catalog.Lobbies.Count);
        Assert.Null(catalog.ByIndex(ServerOptions.MaxLobbies));
    }
}

public class LobbyValidationIsEnforcedAtStartupTests
{
    [Fact]
    public async Task Too_many_lobbies_stops_the_server_rather_than_being_silently_trimmed()
    {
        await using var harness = await ServerHarness.CreateAsync(s =>
        {
            for (var i = 0; i < ServerOptions.MaxLobbies + 1; i++)
                s[$"Server:Lobbies:{i}:Name"] = $"Lobby {i}";
        });

        // Reading the options is what triggers validation, and it is unavoidable: every
        // listener and every dispatch goes through them.
        var monitor = (Microsoft.Extensions.Options.IOptionsMonitor<ServerOptions>)
            harness.Services.GetService(
                typeof(Microsoft.Extensions.Options.IOptionsMonitor<ServerOptions>))!;

        var failure = Assert.ThrowsAny<Exception>(() => _ = monitor.CurrentValue);

        Assert.Contains("At most 10 lobbies", failure.Message);
    }
}
