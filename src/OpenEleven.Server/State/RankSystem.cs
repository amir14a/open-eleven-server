// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>
/// The rank system around the per-match settlement in <see cref="MatchResults"/>:
/// global rank, the weekly ranking archive, monthly seasons with promotion and relegation,
/// and the CMD_GET_RANKING text the ranking screen parses.
/// </summary>
public static class RankSystem
{
    /// <summary>pes2011.exe enum 0x16 order; the ranking screen sends and reads the index.</summary>
    public static readonly string[] Divisions = ["D5", "D4", "D3", "D2", "D1"];

    /// <summary>
    /// Titles on the D5..D1 divisions. PES 2010 keeps its own names (D3C...), which
    /// <see cref="Normalize"/> and the season moves would overwrite.
    /// </summary>
    public static bool Applies(GameProfile profile) => profile != GameProfile.Pes2010Pc;

    /// <summary>The divisions the client knows (plus NONE); anything else breaks its parsers.</summary>
    public static string DivisionName(string? value)
        => value == "NONE" || Divisions.Contains(value) ? value! : "D5";

    /// <summary>
    /// PES 2010's own ladder: D1, D2, D3A, D3B, D3C (it starts in D3C). The internal
    /// PES 2011 tiers must not leak into its search rows or profile screens.
    /// </summary>
    public static string Pes2010DivisionName(string? value) => value switch
    {
        "D1" or "D2" or "D3A" or "D3B" or "D3C" => value!,
        "D3" => "D3A",
        "D4" => "D3B",
        _ => "D3C",
    };

    /// <summary>The division string a title's clients understand.</summary>
    public static string Label(string? value, GameProfile profile)
        => profile == GameProfile.Pes2010Pc ? Pes2010DivisionName(value) : DivisionName(value);

    public const string WeekKey = "rank.week", SeasonKey = "rank.season", InitializedKey = "rank.initialized";

    /// <summary>A never-played profile sits at the start rating, and every profile in a 2011 division.</summary>
    public static void Normalize(Player p, RankOptions rank, GameProfile profile)
    {
        if (p.Stats.MatchCount == 0)
            p.Rating = rank.StartRating;
        // PES 2010 stores D1..D3C; rewriting them to D5 loses the title's own ladder.
        if (Applies(profile) && !Divisions.Contains(p.Division))
            p.Division = "D5";
    }

    /// <summary>PES 2010 rows damaged by the old unconditional D5 normalization.</summary>
    private static void RepairPes2010Divisions(GameDbContext db)
    {
        foreach (var p in db.Players.Where(p => p.Division == "D5" || p.Division == "D4"
                                                || p.Division == "D3" || p.Division == "D2" || p.Division == "D1"))
            p.Division = Pes2010DivisionName(p.Division);
    }

    public static int DivisionIndex(string division) => Math.Max(0, Array.IndexOf(Divisions, division));

    private static readonly string[] Pes2010RankingDivisions = ["D3C", "D3B", "D3A", "D2", "D1"];

    /// <summary>
    /// The divisions by the index the ranking screen sends and reads back in each row.
    /// pes2010.exe (FUN_00729290) stores 4 - index as its D1..D3C enum, so its ladder takes
    /// the same five positions, lowest first.
    /// </summary>
    public static string[] RankingDivisions(GameProfile profile)
        => profile == GameProfile.Pes2010Pc ? Pes2010RankingDivisions : Divisions;

    /// <summary>Monday 00:00 UTC of the week <paramref name="utc"/> falls in.</summary>
    public static DateTime WeekStart(DateTime utc)
    {
        var day = utc.Date;
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }

    public static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static string SeasonOf(DateTime utc) => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>Position by rating among players with at least one match; the rest have rank 0.</summary>
    public static async Task RecomputeRanksAsync(GameDbContext db)
    {
        // ponytail: full re-rank after every rated match; a windowed UPDATE if the player count grows large.
        var players = await db.Players.Include(p => p.Stats).ToListAsync();
        var position = 0;
        foreach (var p in players.Where(p => p.Stats.MatchCount > 0).OrderByDescending(p => p.Rating).ThenBy(p => p.Pid))
            p.Rank = ++position;
        foreach (var p in players.Where(p => p.Stats.MatchCount == 0))
            p.Rank = 0;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Runs once per database: lifts existing ratings onto the start-rating scale (they used
    /// to start at 0) and moves PES 2010 divisions into D1..D5.
    /// </summary>
    public static async Task InitializeAsync(GameDbContext db, RankOptions rank, DateTime now, GameProfile profile)
    {
        // Repair runs independently of the one-time rank initialization so databases created
        // by the old code get their PES 2010 ladder back.
        if (profile == GameProfile.Pes2010Pc)
        {
            RepairPes2010Divisions(db);
            await db.SaveChangesAsync();
        }

        if (await db.Settings.FindAsync(InitializedKey) is not null)
            return;

        foreach (var p in await db.Players.Include(p => p.Stats).ToListAsync())
        {
            p.Rating += rank.StartRating;
            if (Applies(profile) && !Divisions.Contains(p.Division))
                p.Division = "D5";
        }

        db.Settings.Add(new ServerSetting { Key = InitializedKey, Value = "1" });
        Set(db, WeekKey, Unix(WeekStart(now)).ToString(CultureInfo.InvariantCulture));
        Set(db, SeasonKey, SeasonOf(now));
        await db.SaveChangesAsync();
        await RecomputeRanksAsync(db);
    }

    /// <summary>Archives the finished week and closes the finished season. Idempotent.</summary>
    public static async Task RolloverAsync(GameDbContext db, RankOptions rank, DateTime now, GameProfile profile)
    {
        var week = Unix(WeekStart(now)).ToString(CultureInfo.InvariantCulture);
        var storedWeek = (await db.Settings.FindAsync(WeekKey))?.Value;
        if (storedWeek is not null && storedWeek != week)
            await CloseWeekAsync(db, long.Parse(storedWeek, CultureInfo.InvariantCulture));
        if (storedWeek != week)
            Set(db, WeekKey, week);

        var season = SeasonOf(now);
        var storedSeason = (await db.Settings.FindAsync(SeasonKey))?.Value;
        if (storedSeason is not null && storedSeason != season)
            await CloseSeasonAsync(db, rank, storedSeason, now, profile);
        if (storedSeason != season)
            Set(db, SeasonKey, season);

        await db.SaveChangesAsync();
    }

    private static async Task CloseWeekAsync(GameDbContext db, long weekStart)
    {
        var players = await db.Players.Where(p => p.WeeklyMatches > 0).ToListAsync();
        var position = 0;
        foreach (var p in players.OrderByDescending(p => p.Rating).ThenByDescending(p => p.WeeklyWins))
        {
            db.WeeklyRankings.Add(new WeeklyRankingEntry
            {
                WeekStart = weekStart, Pid = p.Pid, Name = p.Name, Rank = ++position,
                Points = p.WeeklyPoints, Matches = p.WeeklyMatches, Wins = p.WeeklyWins,
                Draws = p.WeeklyDraws, Losses = p.WeeklyLosses, Streak = p.WeeklyBestStreak,
                Rating = p.Rating, Division = p.Division,
            });
        }

        foreach (var p in await db.Players.Where(p => p.WeeklyMatches > 0 || p.WeeklyPoints != 0).ToListAsync())
            p.WeeklyPoints = p.WeeklyMatches = p.WeeklyWins = p.WeeklyDraws = p.WeeklyLosses
                = p.WeeklyStreak = p.WeeklyBestStreak = 0;
    }

    /// <summary>
    /// Season end: inside each division, players with enough ranked matches are ordered by
    /// season points; the top share goes up, the bottom share goes down. Ratings drift back
    /// toward the start rating so the new season is open.
    /// </summary>
    private static async Task CloseSeasonAsync(GameDbContext db, RankOptions rank, string season, DateTime now,
        GameProfile profile)
    {
        // PES 2010 has no season promotion/relegation in this server; its ladder must survive.
        if (!Applies(profile))
            return;

        var players = await db.Players.Include(p => p.Stats).ToListAsync();
        var oldRank = players.ToDictionary(p => p.Pid, p => p.Rank);
        var moves = new Dictionary<int, (string Old, string New)>();

        foreach (var group in players.Where(p => p.SeasonMatches >= rank.MinSeasonMatches).GroupBy(p => p.Division))
        {
            var ordered = group.OrderByDescending(p => p.SeasonPoints).ThenByDescending(p => p.Rating).ToList();
            var index = DivisionIndex(group.Key);
            var up = index < Divisions.Length - 1 ? ordered.Count * rank.PromotePercent / 100 : 0;
            var down = index > 0 ? ordered.Count * rank.RelegatePercent / 100 : 0;
            foreach (var p in ordered.Take(up))
                moves[p.Pid] = (p.Division, Divisions[index + 1]);
            foreach (var p in ordered.Skip(ordered.Count - down).Where(p => !moves.ContainsKey(p.Pid)))
                moves[p.Pid] = (p.Division, Divisions[index - 1]);
        }

        foreach (var p in players)
        {
            Normalize(p, rank, profile);
            p.Rating = (int)Math.Round(rank.StartRating + (p.Rating - rank.StartRating) * rank.SeasonRatingCarry);
            p.SeasonPoints = p.SeasonMatches = 0;
            if (moves.TryGetValue(p.Pid, out var move))
                p.Division = move.New;
        }

        await db.SaveChangesAsync();
        await RecomputeRanksAsync(db);

        var date = Unix(now);
        foreach (var p in players.Where(p => moves.ContainsKey(p.Pid)))
        {
            var (from, to) = moves[p.Pid];
            p.PendingDivisionUpdate = string.Join(',', from, oldRank[p.Pid], to, p.Rank, date);
            var promoted = DivisionIndex(to) > DivisionIndex(from);
            db.Notices.Add(new PlayerNotice
            {
                Pid = p.Pid,
                Title = ServerText.Get(promoted ? "promoted.title" : "relegated.title", p.Lang, to),
                Body = ServerText.Get(promoted ? "promoted.body" : "relegated.body", p.Lang, season, from, to),
                Important = true,
            });
        }
    }

    private static void Set(GameDbContext db, string key, string value)
    {
        var row = db.Settings.Local.FirstOrDefault(s => s.Key == key) ?? db.Settings.Find(key);
        if (row is null)
            db.Settings.Add(new ServerSetting { Key = key, Value = value });
        else
            row.Value = value;
    }

    /// <summary>One ranking screen line: what it shows, and what it sorts by.</summary>
    public sealed record Line(int Pid, string Name, int Matches, int Wins, int Draws, int Losses, int Value,
        string Division, int Rating, int Goals = 0, int Streak = 0);

    /// <summary>
    /// The sort of each ranking tab (1..7; 9..15 are the same tabs for the running week), as
    /// the game labels them: each tab sorts by one of the table's columns.
    /// </summary>
    public static int Metric(int type, Line l) => (type > 8 ? type - 8 : type) switch
    {
        1 => l.Matches,
        2 => l.Wins,
        3 => l.Draws,
        4 => l.Losses,
        5 => l.Streak,
        7 => l.Matches == 0 ? 0 : l.Wins * 10000 / l.Matches,
        _ => l.Rating,
    };

    /// <summary>
    /// ranking_data as FUN_005f0d80 (all-time) and FUN_005f11d0 (weekly) parse it: LF lines,
    /// a header, then up to 100 lines of exactly 18 comma fields:
    /// rank, pid, name, (unused), matches, wins, draws, losses, win streak, division index
    /// (enum 0x16), rating, then 7 unused. Win % is computed by the client from wins / matches.
    /// The count line is read as int, char, int (FUN_005f0760) and only the second int is kept,
    /// so it is written "registered/listed"; the client pages and shows it.
    /// All-time header: (unused), count line, "yyyy/MM/dd HH:mm:ss", 8 unused lines.
    /// Weekly header: week start, week end (same date format), (unused), count line.
    /// PES 2010 (FUN_007297a0 all-time, FUN_00729a70 weekly) has the same layout, but splits the
    /// lines with strtok, which skips empty ones, and its dates use dashes (yyyy-MM-dd). Its row
    /// parse (FUN_00729290) reads the fourth field as %d. The last unused line (all-time) or
    /// the one before the count (weekly) is the player's own row there.
    /// </summary>
    public static string Format(IReadOnlyList<Line> ordered, int type, int from, int records,
        DateTime updated, (DateTime Start, DateTime End)? week, int registered = 0,
        GameProfile profile = GameProfile.Pes2011Pc)
    {
        var pes2010 = profile == GameProfile.Pes2010Pc;
        // PES 2010 needs a non-empty unused line; "-" is not a row, so no own row is shown.
        var unused = pes2010 ? "-\n" : "\n";
        var sb = new StringBuilder();
        var count = $"{Math.Max(registered, ordered.Count)}/{ordered.Count}";
        if (week is { } w)
        {
            sb.Append(Date(w.Start)).Append('\n').Append(Date(w.End)).Append('\n').Append(unused)
                .Append(count).Append('\n');
        }
        else
        {
            sb.Append("1\n").Append(count).Append('\n').Append(Date(updated)).Append('\n');
            for (var i = 0; i < 8; i++)
                sb.Append(unused);
        }

        for (var i = from; i < ordered.Count && i < from + Math.Min(records, 100); i++)
        {
            var l = ordered[i];
            // The client wraps this text in ranking_data="...": no commas or quotes inside a name.
            sb.Append(i + 1).Append(',').Append(l.Pid).Append(',').Append(l.Name.Replace(",", " ").Replace("\"", "'"))
                .Append(',').Append(pes2010 ? "0" : "").Append(',').Append(l.Matches).Append(',').Append(l.Wins)
                .Append(',').Append(l.Draws).Append(',').Append(l.Losses).Append(',').Append(l.Streak).Append(',')
                .Append(Math.Max(0, Array.IndexOf(RankingDivisions(profile), Label(l.Division, profile))))
                .Append(',').Append(l.Rating).Append(",0,0,0,0,0,0,0\n");
        }

        return sb.ToString();

        string Date(DateTime d) => d.ToString(pes2010 ? "yyyy-MM-dd HH:mm:ss" : "yyyy/MM/dd HH:mm:ss",
            CultureInfo.InvariantCulture);
    }
}
