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
    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;
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
