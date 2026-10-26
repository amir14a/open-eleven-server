// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Handlers;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class Pes2010PresentationTests
{
    [Theory]
    [InlineData("D1", "D1")]
    [InlineData("D2", "D2")]
    [InlineData("D3", "D3A")]
    [InlineData("D3A", "D3A")]
    [InlineData("D3B", "D3B")]
    [InlineData("D3C", "D3C")]
    [InlineData("D4", "D3B")]
    [InlineData("D5", "D3C")]
    [InlineData("NONE", "D3C")]
    public void Pes2010_uses_its_own_division_ladder(string stored, string expected)
    {
        Assert.Equal(expected, RankSystem.Label(stored, GameProfile.Pes2010Pc));
    }

    [Fact]
    public void Normalizing_a_pes2010_player_keeps_its_division()
    {
        var player = new Player { Division = "D3C", Stats = new PlayerStats { MatchCount = 1 } };
        RankSystem.Normalize(player, new RankOptions(), GameProfile.Pes2010Pc);
        Assert.Equal("D3C", player.Division);
    }

    [Fact]
    public async Task Private_info_entries_carry_every_field_pes2010_requires()
    {
        await using var harness = await Harness();
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            db.Notices.Add(new PlayerNotice
            {
                Pid = 1,
                Title = "Friend request accepted",
                Body = "player-2 is now your friend.",
            });
            await db.SaveChangesAsync();
        }

        var account = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        var reply = Assert.Single(await harness.DispatchAsync(account, "CMD_GET_PRIVATEINFO",
            withFields: r => r.Set("pid", 1)));
        Assert.Equal(1, reply.GetInt32("count"));
        var notice = Assert.Single(reply.GetList("privInfo"));
        // FUN_00731C60 stops at the first missing field; present was the missing one.
        Assert.True(notice.Has("reldate") && notice.Has("iid") && notice.Has("title")
                    && notice.Has("info") && notice.Has("important") && notice.Has("present"));
        Assert.Equal("TRUE", notice.GetString("present"));
        Assert.Equal("FALSE", notice.GetString("important"));
    }

    [Fact]
    public void Pes2011_keeps_its_division_keys()
    {
        Assert.Equal("D2", RankSystem.Label("D2", GameProfile.Pes2011Pc));
        Assert.Equal("D5", RankSystem.Label("D3C", GameProfile.Pes2011Pc));
    }

    [Fact]
    public async Task Search_returns_a_pes2010_division_not_D5()
    {
        await using var harness = await Harness();
        var me = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        me.Pid = 1;

        var reply = Assert.Single(await harness.DispatchAsync(me, "CMD_SEARCH_PLAYER", withFields: r => r
            .Set("option", "PART").Set("pname", "local")));
        var row = Assert.Single(reply.GetList("list"));
        Assert.Equal("Local Player", row.GetString("name"));
        Assert.Equal("D3C", row.GetString("division"));
    }

    [Fact]
    public async Task Player_info_carries_the_five_team_history_for_pes2010()
    {
        await using var harness = await Harness();
        await AddMatchAsync(harness, homeTeam: 46, awayTeam: 50);
        var me = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        me.Pid = 1;

        var info = Assert.Single(await harness.DispatchAsync(me, "CMD_GET_PLAYERINFO", withFields: r => r.Set("pid", 1)));
        Assert.Equal("D3C", info.GetString("div"));
        var wire = harness.Render(info);
        // List form: FUN_00b6e0f0 reads teamLog[0] as element 0 of teamLog=[...].
        Assert.Contains("teamLog=[46,", wire);
        Assert.DoesNotContain("teamLog[0]", wire);
        Assert.Equal(5, CountIndexed(info, "teamLog"));
    }

    [Fact]
    public async Task Player_info_keeps_the_pes2011_shape()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2011Pc");
        var me = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        me.Pid = 1;

        var info = Assert.Single(await harness.DispatchAsync(me, "CMD_GET_PLAYERINFO", withFields: r => r.Set("pid", 1)));
        Assert.False(info.Has("FD_teamLog"));
        Assert.DoesNotContain("FD_teamLog", harness.Render(info));
    }

    [Fact]
    public void Block_and_account_lists_use_the_pes2010_division_too()
    {
        var player = new Player { Pid = 1, Name = "p", Division = "D5", Kind = "NORMAL", Lang = "EN" };
        Assert.Equal("D3C", PlayerPresenter.ListEntry(player, GameProfile.Pes2010Pc).GetString("division"));
        Assert.Equal("D3C", PlayerPresenter.BlockListEntry(player, 0, GameProfile.Pes2010Pc).GetString("division"));
        Assert.Equal("D5", PlayerPresenter.ListEntry(player, GameProfile.Pes2011Pc).GetString("division"));
    }

    private static int CountIndexed(KvMessage message, string key)
        => message.GetValue(key) switch
        {
            IndexedField field => field.Values.Count,
            KvArray array => array.Values.Count,
            _ => 0,
        };

    private static Task<ServerHarness> Harness() => ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2010Pc");

    private static async Task AddMatchAsync(ServerHarness harness, int homeTeam, int awayTeam)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        db.Matches.Add(new MatchRecord
        {
            HomePid = 1,
            AwayPid = 2,
            HomeTeamId = homeTeam,
            AwayTeamId = awayTeam,
            Completed = true,
            Ranked = true,
            PlayedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
