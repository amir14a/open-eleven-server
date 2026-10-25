// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public sealed class PlayerLifecycleTests
{
    [Fact]
    public void Uses_the_client_profile_name_length_limit()
        => Assert.Equal(15, PlayerNamePolicy.MaxLength);

    [Fact]
    public async Task New_account_has_no_profile_then_create_returns_persisted_defaults()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var account = await RegisterAsync(harness, "fresh-account");
        var session = AccountSession(harness, account.Id);

        var empty = (await harness.DispatchAsync(session, "CMD_GET_PLAYERLIST"))[0];
        Assert.Equal("NOERR", empty.GetString("result"));
        Assert.Equal(0, empty.GetInt32("player_count"));
        Assert.Empty(empty.GetList("player_data"));

        var created = (await harness.DispatchAsync(
            session, "CMD_CREATE_PLAYER", withFields: r => r.Set("name", "Fresh Player")))[0];
        Assert.Equal("NOERR", created.GetString("result"));

        var list = (await harness.DispatchAsync(session, "CMD_GET_PLAYERLIST"))[0];
        Assert.Equal(1, list.GetInt32("player_count"));
        var entry = Assert.Single(list.GetList("player_data"));
        Assert.Equal("Fresh Player", entry.GetString("name"));
        // PES 2010 (the harness default): its own entry division, and the configured start rating.
        Assert.Equal("D3C", entry.GetString("division"));
        Assert.Equal(1000, entry.GetInt32("rating"));

        var info = (await harness.DispatchAsync(session, "CMD_GET_PLAYERINFO"))[0];
        Assert.Equal("Fresh Player", info.GetString("name"));
        Assert.Equal("NORMAL", info.GetString("kind"));
        Assert.Equal("EN", info.GetString("lang"));
        Assert.True(Assert.IsType<bool>(info.GetValue("automatch_want")));
        Assert.True(Assert.IsType<bool>(info.GetValue("beginnermark")));
        Assert.True(Assert.IsType<bool>(info.GetValue("enable_chat")));
    }

    [Fact]
    public async Task Rank_system_titles_create_profiles_in_d5_at_the_start_rating()
    {
        await using var harness = await ServerHarness.CreateAsync(s =>
        {
            s["Server:GameProfile"] = "Pes2011Pc";
            s["Server:Rank:StartRating"] = "1200";
        });
        var account = await RegisterAsync(harness, "rank-account");
        var session = AccountSession(harness, account.Id);

        await harness.DispatchAsync(session, "CMD_CREATE_PLAYER", withFields: r => r.Set("name", "Ranked"));

        await using var scope = harness.Services.CreateAsyncScope();
        var player = scope.ServiceProvider.GetRequiredService<GameDbContext>().Players.Single(p => p.Name == "Ranked");
        Assert.Equal(("D5", 1200), (player.Division, player.Rating));
    }

    [Fact]
    public void Unplayed_profiles_are_moved_to_the_start_rating_and_played_ones_are_kept()
    {
        var rank = new RankOptions { StartRating = 1000 };
        var unplayed = new Player { Rating = 500, Division = "D5", Stats = new PlayerStats() };
        var played = new Player { Rating = 742, Division = "D5", Stats = new PlayerStats { MatchCount = 3 } };

        RankSystem.Normalize(unplayed, rank);
        RankSystem.Normalize(played, rank);

        Assert.Equal((1000, 742), (unplayed.Rating, played.Rating));
    }

    [Fact]
    public async Task Create_rejects_second_profile_and_case_insensitive_global_name_collision()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var first = await RegisterAsync(harness, "first-account");
        var second = await RegisterAsync(harness, "second-account");
        var firstSession = AccountSession(harness, first.Id);
        var secondSession = AccountSession(harness, second.Id);

        Assert.Equal("NOERR", (await CreateAsync(harness, firstSession, "Unique Name")).GetString("result"));
        Assert.Equal(
            "ERR_ALREADYEXISTS",
            (await CreateAsync(harness, firstSession, "Another Name")).GetString("result"));
        Assert.Equal(
            "ERR_ALREADYEXISTS",
            (await CreateAsync(harness, secondSession, "unique name")).GetString("result"));
    }

    [Fact]
    public async Task Create_reports_database_failure_for_an_unknown_account()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = AccountSession(harness, int.MaxValue);

        var response = await CreateAsync(harness, session, "Orphan Profile");

        Assert.Equal("ERR_DATABASE", response.GetString("result"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    [InlineData("1234567890123456")]
    public async Task Create_rejects_invalid_names(string name)
    {
        await using var harness = await ServerHarness.CreateAsync();
        var account = await RegisterAsync(harness, $"invalid-{Guid.NewGuid():N}");
        var session = AccountSession(harness, account.Id);

        var response = await CreateAsync(harness, session, name);

        Assert.Equal("ERR_INVALIDLETTER", response.GetString("result"));
    }

    [Fact]
    public async Task Create_accepts_a_name_at_the_client_limit()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var account = await RegisterAsync(harness, "fifteen-character-name");
        var session = AccountSession(harness, account.Id);

        var response = await CreateAsync(harness, session, "123456789012345");

        Assert.Equal("NOERR", response.GetString("result"));
        Assert.Equal("123456789012345", (await GetOwnedPlayerAsync(harness, account.Id)).Name);
    }

    [Fact]
    public async Task Delete_removes_inactive_profile_and_clears_every_account_session()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var account = await RegisterAsync(harness, "delete-account");
        var accountSession = AccountSession(harness, account.Id);
        await CreateAsync(harness, accountSession, "Delete Me");
        var player = await GetOwnedPlayerAsync(harness, account.Id);

        accountSession.Pid = player.Pid;
        accountSession.PlayerName = player.Name;
        accountSession.State = SessionState.PlayerSelected;
        var menuSession = harness.NewSession(ServiceRole.Menu, SessionState.PlayerSelected);
        menuSession.AccountId = account.Id;
        menuSession.Pid = player.Pid;
        menuSession.PlayerName = player.Name;

        var deleted = (await harness.DispatchAsync(accountSession, "CMD_DEL_PLAYER"))[0];

        Assert.Equal("NOERR", deleted.GetString("result"));
        Assert.Equal(0, accountSession.Pid);
        Assert.Equal(SessionState.Authenticated, accountSession.State);
        Assert.Equal(0, menuSession.Pid);
        Assert.Equal(SessionState.Authenticated, menuSession.State);
        Assert.Null(await FindOwnedPlayerAsync(harness, account.Id));
        Assert.Equal(
            "ERR_NOPLAYER",
            (await harness.DispatchAsync(accountSession, "CMD_DEL_PLAYER"))[0].GetString("result"));
    }

    [Theory]
    [InlineData(SessionState.InBlock)]
    [InlineData(SessionState.InRoom)]
    [InlineData(SessionState.Matching)]
    [InlineData(SessionState.InMatch)]
    public async Task Delete_is_refused_while_any_account_session_is_active(SessionState state)
    {
        await using var harness = await ServerHarness.CreateAsync();
        var account = await RegisterAsync(harness, $"active-{state}");
        var accountSession = AccountSession(harness, account.Id);
        await CreateAsync(harness, accountSession, $"Active {state}");
        var player = await GetOwnedPlayerAsync(harness, account.Id);

        var active = harness.NewSession(ServiceRole.Lobby, state);
        active.AccountId = account.Id;
        active.Pid = player.Pid;
        if (state == SessionState.InBlock)
            active.BlockId = 1;
        if (state == SessionState.InRoom)
            active.RoomId = 1;

        var response = (await harness.DispatchAsync(accountSession, "CMD_DEL_PLAYER"))[0];

        Assert.Equal("ERR_DATABASE", response.GetString("result"));
        Assert.NotNull(await FindOwnedPlayerAsync(harness, account.Id));
    }

    [Fact]
    public async Task Profile_update_cannot_modify_another_accounts_player()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var owner = await RegisterAsync(harness, "profile-owner");
        var attacker = await RegisterAsync(harness, "profile-attacker");
        var ownerSession = AccountSession(harness, owner.Id);
        await CreateAsync(harness, ownerSession, "Owned Profile");
        var player = await GetOwnedPlayerAsync(harness, owner.Id);

        var attackerSession = AccountSession(harness, attacker.Id);
        attackerSession.Pid = player.Pid;
        var response = (await harness.DispatchAsync(
            attackerSession,
            "CMD_SET_PLAYERPROFILE",
            withFields: request => request.Set(
                "profile", new KvMessage().Set("intro", "unauthorized"))))[0];

        Assert.Equal("ERR_DATABASE", response.GetString("result"));
        Assert.Equal("", (await GetOwnedPlayerAsync(harness, owner.Id)).Intro);
    }

    private static Session AccountSession(ServerHarness harness, int accountId)
    {
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        session.AccountId = accountId;
        session.Pid = 0;
        session.PlayerName = "";
        return session;
    }

    private static async Task<KvMessage> CreateAsync(
        ServerHarness harness, Session session, string name)
        => (await harness.DispatchAsync(
            session, "CMD_CREATE_PLAYER", withFields: request => request.Set("name", name)))[0];

    private static async Task<Account> RegisterAsync(ServerHarness harness, string gameId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        var (result, account) = await accounts.RegisterAsync(
            gameId, "86d84f975c5afebdea53f5ec3c6abbde", "SERIAL-TEST");
        Assert.Equal(RegistrationResult.Created, result);
        return Assert.IsType<Account>(account);
    }

    private static async Task<Player> GetOwnedPlayerAsync(ServerHarness harness, int accountId)
        => Assert.IsType<Player>(await FindOwnedPlayerAsync(harness, accountId));

    private static async Task<Player?> FindOwnedPlayerAsync(ServerHarness harness, int accountId)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var players = scope.ServiceProvider.GetRequiredService<IPlayerRepository>();
        return (await players.GetForAccountAsync(accountId)).SingleOrDefault();
    }
}
