// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>A player's rating screen line (CMD_GET_GAMEADDPOINT) after a match.</summary>
public sealed record PlayerResult(int AddPoint, int Point, int Rating, int OldRating, int MatchCount);

/// <summary>
/// Opens a match record when the match starts (its id is the client's match_id) and settles
/// it once when it ends: history, stats, rating, points and manner.
/// <list type="bullet">
/// <item>Played to the end: the score decides; a shoot-out decides a draw.</item>
/// <item>A player left (connection dropped, or left the room) while the other stayed: the
/// leaver loses with a disconnect and a manner step, the other wins.</item>
/// <item>P2P broke but both stayed connected (a forfeit nobody left after): no match, nothing changes.</item>
/// </list>
/// Rating and points only move for ranked matches that pass the anti-farming rules
/// (<see cref="RankOptions"/>): same game data on both sides, and a daily cap per opponent.
/// </summary>
public sealed class MatchResults(
    IServiceScopeFactory scopes,
    ILogger<MatchResults> log,
    IOptionsMonitor<ServerOptions>? options = null)
{
    /// <summary>How long a forfeit waits for the lost player to drop before it counts as nobody's fault.</summary>
    public static readonly TimeSpan ForfeitGrace = TimeSpan.FromSeconds(15);

    /// <summary>Manner range; players start in the middle.</summary>
    public const int MannerMin = 1, MannerMax = 5;

    /// <summary>Completed matches that win a manner step back.</summary>
    public const int MannerRecovery = 10;

    private RankOptions Rank => options?.CurrentValue.Rank ?? new RankOptions();

    /// <summary>The two sides as they were when the match ended (pid per GameSide).</summary>
    public static (int Home, int Away) Lineup(Room room)
        => (room.Snapshot().FirstOrDefault(m => m.GameSide == 0)?.Pid ?? 0,
            room.Snapshot().FirstOrDefault(m => m.GameSide == 1)?.Pid ?? 0);

    /// <summary>
    /// The match id both clients ask for while the match is set up (CMD_GET_MATCHID). The
    /// first ask opens the match record, so the id survives a restart and is the history id.
    /// </summary>
    public async Task<int> StartAsync(Room room)
    {
        // Both players ask at the same moment; one record per match.
        await room.MatchIdGate.WaitAsync();
        try
        {
            if (room.MatchId != 0)
                return room.MatchId;

            foreach (var member in room.Snapshot())
                room.ParaHashes[member.Pid] = member.ParaHash;

            var lineup = Lineup(room);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            var record = new MatchRecord
            {
                HomePid = lineup.Home,
                AwayPid = lineup.Away,
                Ranked = room.MatchType == "OC_RANKING",
                Completed = false,
            };
            db.Matches.Add(record);
            await db.SaveChangesAsync();
            room.MatchId = (int)record.Id;
            return room.MatchId;
        }
        finally
        {
            room.MatchIdGate.Release();
        }
    }

    /// <summary>
    /// A player reported losing its peer. Whether that is a quit or a broken link is only known
    /// once we see if the other player's connection drops; until then nothing is recorded.
    /// </summary>
    public void Forfeited(Room room, int stayerPid)
    {
        room.ForfeitStayerPid = stayerPid;
        var lineup = Lineup(room);
        _ = Task.Delay(ForfeitGrace).ContinueWith(_ => RecordAsync(room, lineup, leaverPid: null, noMatch: true));
    }

    /// <summary>
    /// Settles the match if it is still open; <paramref name="leaverPid"/> names the player at
    /// fault, <paramref name="noMatch"/> voids it. Safe to call from several paths: only the first counts.
    /// </summary>
    public async Task RecordAsync(Room room, (int Home, int Away) lineup, int? leaverPid, bool noMatch)
    {
        lock (room)
        {
            if (room.ResultRecorded)
                return;
            room.ResultRecorded = true;
        }

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            var rank = Rank;
            var now = DateTime.UtcNow;

            var ranked = room.MatchType == "OC_RANKING";
            var settings = new KvMessage()
                .Set("match_type", room.MatchType)
                .Set("team_category", room.TeamCategory);
            if (room.GameEnvironment is { } env)
                settings.Set("game_env", env);

            var sheet = room.Sheet;
            var record = (room.MatchId != 0 ? await db.Matches.FindAsync((long)room.MatchId) : null) ?? new MatchRecord();
            if (record.Id == 0)
                db.Matches.Add(record);

            var players = await db.Players.Include(p => p.Stats)
                .Where(p => p.Pid == lineup.Home || p.Pid == lineup.Away)
                .ToDictionaryAsync(p => p.Pid);
            players.TryGetValue(lineup.Home, out var home);
            players.TryGetValue(lineup.Away, out var away);
            foreach (var p in players.Values)
                RankSystem.Normalize(p, rank);

            var (homePk, awayPk) = (sheet.Home[4], sheet.Away[4]);

            // 1 = home won, 0 = away won, 0.5 = draw.
            double? homeScore = noMatch || home is null || away is null ? null
                : leaverPid == lineup.Away ? 1
                : leaverPid == lineup.Home ? 0
                : room.HomeGoals > room.AwayGoals ? 1
                : room.HomeGoals < room.AwayGoals ? 0
                : homePk > awayPk ? 1
                : homePk < awayPk ? 0
                : 0.5;

            var rated = ranked && homeScore is not null
                        && SameGameData(room, lineup)
                        && await RatedTodayAsync(db, lineup, now) < rank.MaxRatedPerOpponentPerDay;

            record.HomePid = lineup.Home;
            record.AwayPid = lineup.Away;
            record.HomeScore = room.HomeGoals;
            record.AwayScore = room.AwayGoals;
            record.HomeTeamId = room.HomeTeamId;
            record.AwayTeamId = room.AwayTeamId;
            record.Disconnected = leaverPid is not null || noMatch;
            record.DisconnectedPid = leaverPid;
            record.Ranked = ranked;
            record.Rated = rated;
            record.Settings = new KvWriter().Write(settings).TrimEnd('\0');
            record.HomePeriods = MatchSheet.Format(sheet.Home);
            record.AwayPeriods = MatchSheet.Format(sheet.Away);
            record.ExtraTime = sheet.ExtraTime;
            record.Penalties = sheet.Penalties;
            record.Events = sheet.Events;
            record.Completed = true;
            record.PlayedAt = now;

            var results = new Dictionary<int, PlayerResult>();
            if (homeScore is { } h)
            {
                // A disconnect counts as a clear defeat, whatever the score said.
                var goalDiff = leaverPid is not null ? 3 : Math.Abs(room.HomeGoals - room.AwayGoals);
                var (homeDelta, awayDelta) = rated
                    ? (RatingDelta(home!, away!, h, goalDiff, rank), RatingDelta(away!, home!, 1 - h, goalDiff, rank))
                    : (0, 0);
                results[home!.Pid] = Settle(home, h, homeDelta, rated, room.HomeGoals, room.AwayGoals, leaverPid, rank, now);
                results[away!.Pid] = Settle(away, 1 - h, awayDelta, rated, room.AwayGoals, room.HomeGoals, leaverPid, rank, now);
            }

            if (sheet.CombinationPid != 0 && players.TryGetValue(sheet.CombinationPid, out var comboPlayer) && !noMatch)
            {
                comboPlayer.Stats.TotalCombination += sheet.TotalCombination;
                comboPlayer.Stats.MaxCombination = Math.Max(comboPlayer.Stats.MaxCombination, sheet.MaxCombination);
            }

            await db.SaveChangesAsync();
            if (rated)
                await RankSystem.RecomputeRanksAsync(db);
            room.LastResults = results;

            log.LogInformation(
                "Room {RoomId} result: pid {Home} {HomeGoals}-{AwayGoals} pid {Away}{Pk}{Note}{Rated}",
                room.Id, lineup.Home, room.HomeGoals, room.AwayGoals, lineup.Away,
                homePk + awayPk > 0 ? $" (pens {homePk}-{awayPk})" : "",
                noMatch ? " (no match: P2P lost, nobody left)"
                : leaverPid is { } l ? $" (pid {l} left: counted as a disconnect loss)" : "",
                ranked && !rated && homeScore is not null ? " [unrated: anti-farming rule]" : "");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not record the result of room {RoomId}", room.Id);
        }
    }

    /// <summary>Both players reported the same para_hash at login (unknown counts as same).</summary>
    private bool SameGameData(Room room, (int Home, int Away) lineup)
    {
        if (!Rank.RequireSameParaHash)
            return true;

        var a = room.ParaHashes.GetValueOrDefault(lineup.Home, "");
        var b = room.ParaHashes.GetValueOrDefault(lineup.Away, "");
        return a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<int> RatedTodayAsync(GameDbContext db, (int Home, int Away) lineup, DateTime now)
    {
        var day = now.Date;
        return db.Matches.CountAsync(m => m.Rated && m.Completed && m.PlayedAt >= day
                                          && ((m.HomePid == lineup.Home && m.AwayPid == lineup.Away)
                                              || (m.HomePid == lineup.Away && m.AwayPid == lineup.Home)));
    }

    /// <summary>
    /// Elo with a larger K while a player is new, and a margin-of-victory factor: a 4-0 moves
    /// more than a 1-0, with diminishing returns (1 + ln(1 + goal difference) / 2, at most 2).
    /// </summary>
    private static int RatingDelta(Player p, Player opponent, double score, int goalDiff, RankOptions rank)
    {
        var expected = 1 / (1 + Math.Pow(10, (opponent.Rating - p.Rating) / 400.0));
        var k = p.Stats.MatchCount < rank.SettledAfterMatches ? rank.KNew : rank.KSettled;
        var margin = score == 0.5 ? 1 : Math.Min(2, 1 + Math.Log(1 + goalDiff) / 2);
        return (int)Math.Round(k * margin * (score - expected));
    }

    private static PlayerResult Settle(Player p, double score, int ratingDelta, bool rated,
        int goals, int against, int? leaverPid, RankOptions rank, DateTime now)
    {
        var s = p.Stats;
        var oldRating = p.Rating;
        var addPoint = !rated ? 0 : score switch
        {
            1 => rank.WinPoints + (against == 0 && leaverPid is null ? rank.CleanSheetBonus : 0),
            0.5 => rank.DrawPoints,
            _ => 0,
        };

        p.Rating = Math.Max(0, p.Rating + ratingDelta);
        p.Point += addPoint;
        p.LastPlayedAt = now;
        s.MatchCount++;
        s.Goals += goals;
        s.GoalsAgainst += against;

        if (rated)
        {
            p.WeeklyPoints += addPoint;
            p.WeeklyMatches++;
            p.SeasonPoints += addPoint;
            p.SeasonMatches++;
            if (score == 1) p.WeeklyWins++;
            else if (score == 0) p.WeeklyLosses++;
            else p.WeeklyDraws++;
            p.WeeklyStreak = score == 1 ? p.WeeklyStreak + 1 : 0;
            p.WeeklyBestStreak = Math.Max(p.WeeklyBestStreak, p.WeeklyStreak);
        }

        if (score == 1)
        {
            s.WinCount++;
            s.ContinuousWins++;
            s.MaxContinuousWins = Math.Max(s.MaxContinuousWins, s.ContinuousWins);
            if (leaverPid is not null)
                s.DisconnectWins++;
        }
        else
        {
            s.ContinuousWins = 0;
            if (score == 0)
                s.LoseCount++;
            else
                s.DrawCount++;
        }

        if (leaverPid == p.Pid)
        {
            s.DisconnectCount++;
            s.DisconnectLosses++;
            p.Manner = Math.Max(MannerMin, p.Manner - 1);
            p.MannerProgress = 0;
        }
        else if (++p.MannerProgress >= MannerRecovery)
        {
            p.Manner = Math.Min(MannerMax, p.Manner + 1);
            p.MannerProgress = 0;
        }

        return new PlayerResult(addPoint, p.Point, p.Rating, oldRating, s.MatchCount);
    }
}
