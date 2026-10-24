// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;
using Xunit;

namespace OpenEleven.Server.Tests;

public sealed class MatchResultsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;

    public MatchResultsTests()
    {
        _connection.Open();
        _services = new ServiceCollection()
            .AddDbContext<GameDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        db.Database.EnsureCreated();
        foreach (var (pid, name) in new[] { (1, "home"), (2, "away") })
        {
            var account = new Account { GameId = name, PasswordHash = "x" };
            account.Players.Add(new Player { Pid = pid, Name = name, NormalizedName = name.ToUpperInvariant(), Rating = 500 });
            db.Accounts.Add(account);
        }

        db.SaveChanges();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private MatchResults Results => new(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MatchResults>.Instance);

    private Player Load(int pid)
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<GameDbContext>()
            .Players.Include(p => p.Stats).Single(p => p.Pid == pid);
    }

    [Fact]
    public async Task Played_match_goes_by_the_score_and_is_settled_once()
    {
        var room = new Room(1, "r", 1, 2, ServiceRole.Menu, 1) { MatchType = "OC_RANKING", HomeGoals = 0, AwayGoals = 2 };

        await Results.RecordAsync(room, (1, 2), leaverPid: null, noMatch: false);
        await Results.RecordAsync(room, (1, 2), leaverPid: null, noMatch: false);

        // New players' K (40) times the 2-goal margin factor (1 + ln 3 / 2) times 0.5 = 31;
        // the winner kept a clean sheet: 3 points + 1.
        var (home, away) = (Load(1), Load(2));
        Assert.Equal((0, 1, 469), (home.Stats.WinCount, home.Stats.LoseCount, home.Rating));
        Assert.Equal((1, 0, 531, 4), (away.Stats.WinCount, away.Stats.LoseCount, away.Rating, away.Point));
        Assert.Equal((1, 4, 4), (away.Stats.MatchCount, away.WeeklyPoints, away.SeasonPoints));
        Assert.Equal((31, 531), (room.LastResults[2].Rating - room.LastResults[2].OldRating, room.LastResults[2].Rating));
    }

    [Fact]
    public async Task Leaver_loses_with_a_disconnect_even_when_ahead()
    {
        var room = new Room(1, "r", 1, 2, ServiceRole.Menu, 1) { MatchType = "OC_RANKING", HomeGoals = 3, AwayGoals = 0 };

        await Results.RecordAsync(room, (1, 2), leaverPid: 1, noMatch: false);

        var (home, away) = (Load(1), Load(2));
        Assert.Equal((1, 1, 1), (home.Stats.LoseCount, home.Stats.DisconnectCount, home.Stats.DisconnectLosses));
        Assert.Equal((1, 1, 0), (away.Stats.WinCount, away.Stats.DisconnectWins, away.Stats.DisconnectCount));
    }

    [Fact]
    public async Task Friendly_is_stored_with_its_settings_but_left_out_of_the_results_list()
    {
        var room = new Room(1, "r", 1, 2, ServiceRole.Menu, 1)
        {
            MatchType = "OC_FREE", HomeGoals = 1, AwayGoals = 0,
            GameEnvironment = new KvMessage().Set("gametime", "10MINUTES").Set("substitution", 3),
        };

        await Results.RecordAsync(room, (1, 2), leaverPid: null, noMatch: false);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var stored = db.Matches.Single();
        Assert.False(stored.Ranked);
        Assert.Contains("gametime=\"10MINUTES\"", stored.Settings);
        Assert.Contains("match_type=\"OC_FREE\"", stored.Settings);
        Assert.Empty(await new MatchRepository(db).GetRecentAsync(1, 10));
        Assert.Equal(500, Load(1).Rating);
    }

    [Fact]
    public async Task Rating_stops_moving_after_the_daily_cap_against_the_same_opponent()
    {
        for (var i = 0; i < 4; i++)
        {
            var room = new Room(1, "r", 1, 2, ServiceRole.Menu, 1) { MatchType = "OC_RANKING", HomeGoals = 1, AwayGoals = 0 };
            await Results.RecordAsync(room, (1, 2), leaverPid: null, noMatch: false);
        }

        using var scope = _services.CreateScope();
        var rated = scope.ServiceProvider.GetRequiredService<GameDbContext>().Matches.Select(m => m.Rated).ToList();
        Assert.Equal([true, true, true, false], rated);
        Assert.Equal(4, Load(1).Stats.WinCount);
    }

    [Fact]
    public async Task Different_game_data_makes_the_match_unrated()
    {
        var room = new Room(1, "r", 1, 2, ServiceRole.Menu, 1) { MatchType = "OC_RANKING", HomeGoals = 1, AwayGoals = 0 };
        room.ParaHashes[1] = "aaaa";
        room.ParaHashes[2] = "bbbb";

        await Results.RecordAsync(room, (1, 2), leaverPid: null, noMatch: false);

        Assert.Equal((500, 1), (Load(1).Rating, Load(1).Stats.WinCount));
    }

    [Fact]
    public async Task Shootout_decides_a_draw_and_periods_are_stored()
    {
        var room = new Room(1, "r", 1, 2, ServiceRole.Menu, 1) { MatchType = "OC_RANKING", HomeGoals = 1, AwayGoals = 1 };
        room.Sheet.Goal(false, "1ST", "");
        room.Sheet.Goal(true, "EX1ST", "");
        room.Sheet.Goal(true, "PK", "");

        await Results.RecordAsync(room, (1, 2), leaverPid: null, noMatch: false);

        using var scope = _services.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<GameDbContext>().Matches.Single();
        Assert.Equal(("1,0,0,0,0", "0,0,1,0,1", true, true), (stored.HomePeriods, stored.AwayPeriods, stored.ExtraTime, stored.Penalties));
        Assert.Equal(1, Load(2).Stats.WinCount);
    }

    [Fact]
    public async Task Season_end_promotes_the_top_and_reports_it_once()
    {
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            foreach (var p in db.Players.Include(p => p.Stats))
            {
                p.Division = "D3";
                p.Stats.MatchCount = 10;
                p.SeasonMatches = 10;
                p.SeasonPoints = p.Pid == 1 ? 30 : 3;
                p.WeeklyMatches = 2;
                p.WeeklyPoints = p.Pid == 1 ? 6 : 0;
            }
            db.Settings.Add(new ServerSetting { Key = RankSystem.WeekKey, Value = "1000" });
            db.Settings.Add(new ServerSetting { Key = RankSystem.SeasonKey, Value = "2026-09" });
            db.SaveChanges();

            await RankSystem.RolloverAsync(db, new RankOptions { PromotePercent = 50, RelegatePercent = 50 }, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
        }

        var (top, bottom) = (Load(1), Load(2));
        Assert.Equal(("D2", "D4"), (top.Division, bottom.Division));
        Assert.StartsWith("D3,", top.PendingDivisionUpdate);
        Assert.Equal((0, 0), (top.SeasonPoints, top.WeeklyPoints));

        using var check = _services.CreateScope();
        var week = check.ServiceProvider.GetRequiredService<GameDbContext>().WeeklyRankings.OrderBy(r => r.Rank).ToList();
        Assert.Equal([1, 2], week.Select(r => r.Pid));
    }

    [Fact]
    public void Ranking_text_has_the_header_and_18_fields_per_line()
    {
        var lines = new[] { new RankSystem.Line(7, "ma,rc", 10, 6, 2, 2, 20, "D2", 1100, Streak: 4) };

        var text = RankSystem.Format(lines, 1, 0, 100, new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc), null, registered: 9).Split('\n');

        // int, char, int: the client keeps the second number.
        Assert.Equal(("9/1", "2026/10/24 12:00:00"), (text[1], text[2]));
        var fields = text[11].Split(',');
        Assert.Equal(18, fields.Length);
        Assert.Equal(["1", "7", "ma rc", "", "10", "6", "2", "2", "4", "3", "1100"], fields[..11]);
    }

    [Fact]
    public async Task Ranking_query_serves_all_time_running_week_and_archived_week()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        foreach (var p in db.Players.Include(p => p.Stats))
        {
            p.Stats.MatchCount = p.Pid == 1 ? 9 : 4;
            p.WeeklyMatches = p.Pid == 1 ? 1 : 3;
        }
        db.WeeklyRankings.Add(new WeeklyRankingEntry { WeekStart = 1_790_000_000, Pid = 2, Name = "away", Rank = 1, Points = 6, Matches = 2 });
        db.SaveChanges();
        var query = new RankingQuery(db);
        var now = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);

        // Tab 1 sorts by matches; 9 is the same tab for the running week; weekid = index 0 = newest archive.
        var allTime = (await query.RenderAsync("type=1&division=6&from=0&records=100&pid=1&flag=0", now)).Split('\n');
        var thisWeek = (await query.RenderAsync("type=9&division=6&from=0&records=100&pid=1&flag=0", now)).Split('\n');
        var archived = (await query.RenderAsync("type=6&division=6&from=0&records=100&weekid=0&pid=0&flag=0", now)).Split('\n');

        Assert.StartsWith("1,1,home,,9,", allTime[11]);
        Assert.StartsWith("1,2,away,,3,", thisWeek[11]);
        Assert.Equal("2/1", archived[3]);             // weekly layout: count line 4th
        Assert.StartsWith("1,2,away,,2,", archived[4]);
    }

    [Fact]
    public async Task No_match_records_history_but_changes_nobody()
    {
        var room = new Room(1, "r", 1, 2, ServiceRole.Menu, 1) { MatchType = "OC_RANKING", HomeGoals = 1, AwayGoals = 0 };

        await Results.RecordAsync(room, (1, 2), leaverPid: null, noMatch: true);

        Assert.Equal((0, 500), (Load(1).Stats.MatchCount, Load(1).Rating));
        Assert.Equal((0, 500), (Load(2).Stats.MatchCount, Load(2).Rating));
    }
}
