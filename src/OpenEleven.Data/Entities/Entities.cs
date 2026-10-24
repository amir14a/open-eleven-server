// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.ComponentModel.DataAnnotations;

namespace OpenEleven.Data.Entities;

public sealed class Account
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string GameId { get; set; } = "";

    /// <summary>
    /// The hash the client presents, stored as supplied at registration. It is compared,
    /// never reversed, and is never the plaintext password.
    /// </summary>
    [MaxLength(255)]
    public string PasswordHash { get; set; } = "";

    /// <summary>
    /// Product serial, supplied when the account is registered. The client presents its
    /// own copy as <c>tmpRegcode</c> in MSG_REQAUTH and the two must match. A serial may
    /// be shared by multiple accounts, so it is an extra check and never an account key.
    /// </summary>
    [MaxLength(64)]
    public string? RegCode { get; set; }

    /// <summary>Cleared atomically by the first successful binary authentication.</summary>
    public bool FirstLogin { get; set; } = true;

    public bool Banned { get; set; }

    [MaxLength(255)]
    public string? BanReason { get; set; }

    // Stored as UTC DateTime rather than DateTimeOffset: SQLite cannot ORDER BY or
    // index a DateTimeOffset column, and these are all ordered or filtered on.
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    /// <summary>szPatchVersion / szDLContentsVersion the client reported at its last login.</summary>
    [MaxLength(16)]
    public string PatchVersion { get; set; } = "";

    [MaxLength(16)]
    public string DlcVersion { get; set; } = "";

    public List<Player> Players { get; set; } = new();
}

public sealed class Player
{
    public int Pid { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }

    [MaxLength(15)]
    public string Name { get; set; } = "";

    /// <summary>
    /// Invariant uppercase form of <see cref="Name"/> used for global,
    /// case-insensitive uniqueness across database providers.
    /// </summary>
    [MaxLength(64)]
    public string NormalizedName { get; set; } = "";

    [MaxLength(8)]
    public string Division { get; set; } = "D3C";

    [MaxLength(16)]
    public string Kind { get; set; } = "NORMAL";

    [MaxLength(4)]
    public string Lang { get; set; } = "EN";

    [MaxLength(128)]
    public string Intro { get; set; } = "";

    public int Rating { get; set; }
    public int Point { get; set; }
    public int Rank { get; set; }
    public int Manner { get; set; } = 3;
    public int Country { get; set; }
    public int Area { get; set; }
    public int BirthMonth { get; set; }
    public int BirthDay { get; set; }
    public int FavoriteTeam { get; set; }
    public int FavoritePlayer { get; set; }

    [MaxLength(16)]
    public string SelfReportLevel { get; set; } = "PRO";

    [MaxLength(8)]
    public string PositionWant { get; set; } = "CF";

    /// <summary>13 YES/NO flags, stored as a compact bitmask.</summary>
    public int DesiredPositionMask { get; set; }

    public bool AutoMatchWant { get; set; } = true;
    public bool BeginnerMark { get; set; } = true;
    public bool ChatEnabled { get; set; } = true;

    /// <summary>
    /// PES 2011 CMD_SET_QUICKMATCHOPTION values, comma-separated in order: opp_lang,
    /// oc_is_rating_rank, oc_is_rating_free, ml_is_division. Empty means the defaults.
    /// </summary>
    [MaxLength(64)]
    public string QuickMatchOptions { get; set; } = "";

    /// <summary>
    /// Competition the player entered with CMD_JOIN_COMPLG, 0 for none. One at a time: the
    /// client knows ERR_COMPE_JOIN_OTHERCOMPE for a second entry.
    /// </summary>
    public int CompetitionId { get; set; }

    public DateTime? LastPlayedAt { get; set; }

    /// <summary>Completed matches since manner last moved; ten clean ones win a step back.</summary>
    public int MannerProgress { get; set; }

    // Ranked results of the running week and month (season). The week is archived to
    // WeeklyRankings and the season decides promotion and relegation when they roll over.
    public int WeeklyPoints { get; set; }
    public int WeeklyMatches { get; set; }
    public int WeeklyWins { get; set; }
    public int WeeklyDraws { get; set; }
    public int WeeklyLosses { get; set; }

    /// <summary>Rated wins in a row this week, and the best such run (the weekly streak column).</summary>
    public int WeeklyStreak { get; set; }
    public int WeeklyBestStreak { get; set; }

    public int SeasonPoints { get; set; }
    public int SeasonMatches { get; set; }

    /// <summary>
    /// Division change from the last season end not yet shown to the player, as
    /// "olddiv,oldrank,newdiv,newrank,unixdate". Empty when there is nothing to show.
    /// </summary>
    [MaxLength(64)]
    public string PendingDivisionUpdate { get; set; } = "";

    public PlayerStats Stats { get; set; } = new();
}

public sealed class PlayerStats
{
    public int Id { get; set; }
    public int PlayerId { get; set; }

    public int MatchCount { get; set; }
    public int WinCount { get; set; }
    public int LoseCount { get; set; }
    public int DrawCount { get; set; }
    public int ContinuousWins { get; set; }
    public int MaxContinuousWins { get; set; }
    public int DisconnectCount { get; set; }
    public int DisconnectWins { get; set; }
    public int DisconnectLosses { get; set; }
    public int Goals { get; set; }
    public int GoalsAgainst { get; set; }
    public int TotalCombination { get; set; }
    public int MaxCombination { get; set; }
}

public sealed class MatchRecord
{
    public long Id { get; set; }
    public int HomePid { get; set; }
    public int AwayPid { get; set; }
    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
    public int HomeTeamId { get; set; }
    public int AwayTeamId { get; set; }
    public bool Disconnected { get; set; }
    public int? DisconnectedPid { get; set; }

    /// <summary>Ranking match (rating and points moved) rather than a friendly.</summary>
    public bool Ranked { get; set; }

    /// <summary>Room options and the CMD_SET_GAMEENV record, in the client's key=value text.</summary>
    public string Settings { get; set; } = "";

    /// <summary>
    /// Created when the match starts (its Id is the client's match_id) and completed when it
    /// ends. Only completed rows are history.
    /// </summary>
    public bool Completed { get; set; } = true;

    /// <summary>Goals per period, "1st,2nd,ex1st,ex2nd,pk".</summary>
    [MaxLength(32)]
    public string HomePeriods { get; set; } = "";

    [MaxLength(32)]
    public string AwayPeriods { get; set; } = "";

    public bool ExtraTime { get; set; }
    public bool Penalties { get; set; }

    /// <summary>Rating and points moved (ranked, not voided by the anti-farming rules).</summary>
    public bool Rated { get; set; }

    /// <summary>Goals and cards as reported by the match host, one per line (see MatchEvents).</summary>
    public string Events { get; set; } = "";

    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;
}

public enum FriendState
{
    // Names and order are pes2011.exe enum 0x18.
    Friend = 0,
    SentReq = 1,
    RecievedReq = 2,
}

/// <summary>One side of a friendship or friend request; each pair has a row per player.</summary>
public sealed class Friendship
{
    public int Id { get; set; }
    public int Pid { get; set; }
    public int OtherPid { get; set; }
    public FriendState State { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class BlacklistEntry
{
    public int Id { get; set; }
    public int Pid { get; set; }
    public int TargetPid { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A short mail between players. Sender and recipient each delete their own copy; the row
/// goes once both have.
/// </summary>
public sealed class ShortMail
{
    public int Id { get; set; }
    public int FromPid { get; set; }
    public int ToPid { get; set; }

    [MaxLength(192)]
    public string Subject { get; set; } = "";

    [MaxLength(1536)]
    public string Body { get; set; } = "";

    public bool Read { get; set; }
    public bool DeletedBySender { get; set; }
    public bool DeletedByRecipient { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A message for one player, served by CMD_GET_PRIVATEINFO.</summary>
public sealed class PlayerNotice
{
    public int Id { get; set; }
    public int Pid { get; set; }

    [MaxLength(64)]
    public string Title { get; set; } = "";

    [MaxLength(512)]
    public string Body { get; set; } = "";

    public bool Important { get; set; }

    /// <summary>Already shown to the player; kept (soft-deleted) rather than removed.</summary>
    public bool Delivered { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A finished week's ranking line, kept for the weekly ranking screens.</summary>
public sealed class WeeklyRankingEntry
{
    public long Id { get; set; }

    /// <summary>Monday 00:00 UTC the week started, in unix seconds.</summary>
    public long WeekStart { get; set; }

    public int Pid { get; set; }

    [MaxLength(15)]
    public string Name { get; set; } = "";

    public int Rank { get; set; }
    public int Points { get; set; }
    public int Matches { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public int Streak { get; set; }
    public int Rating { get; set; }

    [MaxLength(8)]
    public string Division { get; set; } = "";
}

/// <summary>Small server bookkeeping values (last processed week and season).</summary>
public sealed class ServerSetting
{
    [MaxLength(64)]
    public string Key { get; set; } = "";

    [MaxLength(256)]
    public string Value { get; set; } = "";
}

/// <summary>News item served by CMD_GET_INFORMATIONLIST. Editable without a redeploy.</summary>
public sealed class InformationItem
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Subject { get; set; } = "";

    [MaxLength(512)]
    public string Body { get; set; } = "";

    public bool Important { get; set; }
    public bool Present { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
}
