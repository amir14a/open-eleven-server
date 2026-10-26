// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Configuration;

public enum LiveSettingKind { Text, LongText, Number, Decimal, Toggle }

/// <summary>One value the admin portal can change; <see cref="Key"/> is its configuration path.</summary>
public sealed record LiveSetting(string Group, string Key, string Label, LiveSettingKind Kind, Func<ServerOptions, object> Read,
    string Help = "", int MaxLength = 256)
{
    public string Current(ServerOptions options) => Read(options) switch
    {
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var v => v.ToString() ?? "",
    };

    /// <summary>The posted value in configuration form, or null when it is not one this setting accepts.</summary>
    public string? Normalize(string? posted) => Kind switch
    {
        LiveSettingKind.Toggle => posted is "true" ? "true" : "false",
        LiveSettingKind.Number => int.TryParse(posted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null,
        LiveSettingKind.Decimal => double.TryParse(posted, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString(CultureInfo.InvariantCulture) : null,
        _ => (posted ?? "").Trim() is var text && text.Length <= MaxLength ? text : null,
    };
}

/// <summary>
/// The settings that apply while the server runs. Anything read once at startup (addresses,
/// ports, HTTPS, the database, the service list) is left to server.yaml. Lists are only edited
/// entry by entry: a configuration layer can change lobby N but cannot add or remove one.
/// </summary>
public static class LiveSettingCatalog
{
    public static IReadOnlyList<LiveSetting> For(ServerOptions options, GameProfile game)
    {
        var list = new List<LiveSetting>
        {
            new("Website", "Server:Site:Announcement", "Home page announcement", LiveSettingKind.LongText, o => o.Site.Announcement,
                "Shown at the top of the home page. Empty shows nothing."),
        };

        for (var i = 0; i < options.Lobbies.Count; i++)
        {
            var at = i;
            var group = $"Lobby {i + 1}";
            list.Add(new(group, $"Server:Lobbies:{i}:Name", "Name", LiveSettingKind.Text, o => o.Lobbies[at].Name, MaxLength: 32));
            // Not Enabled: lobby ids follow the enabled entries' positions, so hiding one live
            // would renumber the lobbies players are standing in.
            list.Add(new(group, $"Server:Lobbies:{i}:MaxPlayers", "Max players", LiveSettingKind.Number, o => o.Lobbies[at].MaxPlayers));
        }

        if (RankSystem.Applies(game))
        {
            const string rank = "Rank system";
            list.AddRange(
            [
                new(rank, "Server:Rank:StartRating", "Start rating", LiveSettingKind.Number, o => o.Rank.StartRating, "Rating of a new player."),
                new(rank, "Server:Rank:KNew", "K (new players)", LiveSettingKind.Number, o => o.Rank.KNew, "Rating swing in a player's first matches."),
                new(rank, "Server:Rank:KSettled", "K (settled)", LiveSettingKind.Number, o => o.Rank.KSettled),
                new(rank, "Server:Rank:SettledAfterMatches", "Settled after matches", LiveSettingKind.Number, o => o.Rank.SettledAfterMatches),
                new(rank, "Server:Rank:WinPoints", "Points for a win", LiveSettingKind.Number, o => o.Rank.WinPoints),
                new(rank, "Server:Rank:DrawPoints", "Points for a draw", LiveSettingKind.Number, o => o.Rank.DrawPoints),
                new(rank, "Server:Rank:CleanSheetBonus", "Clean sheet bonus", LiveSettingKind.Number, o => o.Rank.CleanSheetBonus),
                new(rank, "Server:Rank:MaxRatedPerOpponentPerDay", "Rated matches per opponent per day", LiveSettingKind.Number, o => o.Rank.MaxRatedPerOpponentPerDay, "Stops two friends farming points."),
                new(rank, "Server:Rank:RequireSameParaHash", "Rate only same game data", LiveSettingKind.Toggle, o => o.Rank.RequireSameParaHash),
                new(rank, "Server:Rank:PromotePercent", "Promoted per season (%)", LiveSettingKind.Number, o => o.Rank.PromotePercent),
                new(rank, "Server:Rank:RelegatePercent", "Relegated per season (%)", LiveSettingKind.Number, o => o.Rank.RelegatePercent),
                new(rank, "Server:Rank:MinSeasonMatches", "Matches to move division", LiveSettingKind.Number, o => o.Rank.MinSeasonMatches),
                new(rank, "Server:Rank:SeasonRatingCarry", "Rating kept at season end", LiveSettingKind.Decimal, o => o.Rank.SeasonRatingCarry, "0 to 1: the share of the distance from the start rating a player keeps."),
                new(rank, "Server:Rank:HistoryLimit", "Matches in the in-game history", LiveSettingKind.Number, o => o.Rank.HistoryLimit),
            ]);
        }

        const string rules = "Game rules";
        list.AddRange(
        [
            new(rules, "Server:Protocol:RequireSameParaHashToJoin", "Same game data to join a room", LiveSettingKind.Toggle, o => o.Protocol.RequireSameParaHashToJoin, "Keeps modified and unmodified game data apart."),
            new(rules, "Server:Protocol:PlayerSearchLimit", "Player search results", LiveSettingKind.Number, o => o.Protocol.PlayerSearchLimit),
            new(rules, "Server:Protocol:PendingLoginLifetimeSeconds", "Login window (seconds)", LiveSettingKind.Number, o => o.Protocol.PendingLoginLifetimeSeconds, "How long a web login lets the game connect."),
        ]);

        return list;
    }
}
