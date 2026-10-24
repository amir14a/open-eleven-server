// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Turns a stored player into the field sets the client expects. Kept separate from the
/// handlers so PLAYERLIST and PLAYERINFO cannot drift apart.
/// </summary>
public static class PlayerPresenter
{
    public const int DesiredPositionCount = 13;

    public static KvMessage ListEntry(Player p) => new KvMessage()
        .Set("pid", p.Pid)
        .Set("date", p.LastPlayedAt is { } played
            ? new DateTimeOffset(DateTime.SpecifyKind(played, DateTimeKind.Utc)).ToUnixTimeSeconds()
            : 0)
        .Set("point", p.Point)
        .Set("name", p.Name)
        .Set("match_num", p.Stats.MatchCount)
        .Set("division", p.Division)
        .Set("rating", p.Rating);

    public static KvMessage BlockListEntry(Player p, int roomId) => new KvMessage()
        .Set("pid", p.Pid)
        .Set("name", p.Name)
        .Set("division", p.Division)
        .Set("room_id", roomId)
        .Set("point", p.Point)
        .Set("rating", p.Rating)
        .Set("mcount", p.Stats.MatchCount)
        .Set("win", p.Stats.WinCount)
        .Set("lose", p.Stats.LoseCount)
        .Set("draw", p.Stats.DrawCount)
        .Set("kind", p.Kind)
        .Set("lang", p.Lang);

    /// <summary>
    /// Fills a CMD_GET_PLAYERINFO reply. Field order follows the reference server.
    /// <paramref name="recentTeams"/> fills teamLog (5 entries, newest first; the favourite team
    /// when the player has no history), and the versions are the ones the player's client
    /// reported at its last login.
    /// </summary>
    public static KvMessage FillPlayerInfo(KvMessage message, Player p, string? division = null, bool listSyntax = false,
        IReadOnlyList<int>? recentTeams = null)
    {
        var s = p.Stats;
        var teams = new object?[5];
        for (var i = 0; i < teams.Length; i++)
            teams[i] = recentTeams is { Count: > 0 } ? (i < recentTeams.Count ? recentTeams[i] : 0) : (i == 0 ? p.FavoriteTeam : 0);
        static string Version(string? v) => string.IsNullOrWhiteSpace(v) ? "1.00" : v;

        return message
            .Set("name", p.Name)
            .Set("div", division ?? p.Division)
            .Set("match_num", s.MatchCount)
            .Set("win_num", s.WinCount)
            .Set("lose_num", s.LoseCount)
            .Set("draw_num", s.DrawCount)
            .Set("contWin_num", s.ContinuousWins)
            .Set("contWinMax_num", s.MaxContinuousWins)
            .Set("disconnect_num", s.DisconnectCount)
            .Set("disconWin_num", s.DisconnectWins)
            .Set("disconLose_num", s.DisconnectLosses)
            .Set("birthmonth", p.BirthMonth)
            .Set("birthday", p.BirthDay)
            .Set("country", p.Country)
            .Set("area", p.Area)
            .Set("favoriteTeam", p.FavoriteTeam)
            .Set("favoritePlayer", p.FavoritePlayer)
            .Set("intro", p.Intro)
            .Set("kind", p.Kind)
            .Set("rating", p.Rating)
            .Set("manner", p.Manner)
            .Set("point", p.Point)
            .Set("goal", s.Goals)
            .Set("lostGoal", s.GoalsAgainst)
            .Set("rank", p.Rank)
            .Set("lang", p.Lang)
            .Set("enable_chat", p.ChatEnabled)
            .Set("teamLog", Indexed(listSyntax, teams))
            .Set("total_combination", s.TotalCombination)
            .Set("max_combination", s.MaxCombination)
            .Set("dlcontent", Version(p.Account?.DlcVersion))
            .Set("patch_version", Version(p.Account?.PatchVersion))
            .Set("selfreport_level", p.SelfReportLevel)
            .Set("automatch_want", p.AutoMatchWant)
            .Set("beginnermark", p.BeginnerMark)
            .Set("position_want", p.PositionWant)
            .Set("desired_position", listSyntax
                ? new KvArray(DesiredPositions(p.DesiredPositionMask).Values)
                : DesiredPositions(p.DesiredPositionMask))
            .Set("list_num", 0);
    }

    // PES 2010 reads flat key[i]=v pairs; the PES 2011 tokenizer (FUN_00c79680) only accepts
    // the bracketed key=[a,b,c] form, as it does for ulist and player_data.
    private static object Indexed(bool listSyntax, params object?[] values)
        => listSyntax ? new KvArray(values) : new IndexedField(values);

    /// <summary>Expands the stored bitmask into the 13 YES/NO flags the client reads.</summary>
    public static IndexedField DesiredPositions(int mask)
    {
        var values = new object?[DesiredPositionCount];
        for (var i = 0; i < DesiredPositionCount; i++)
            values[i] = (mask & (1 << i)) != 0 ? "YES" : "NO";
        return new IndexedField(values);
    }
}
