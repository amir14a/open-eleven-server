// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data.Entities;

namespace OpenEleven.Data.Repositories;

public interface IPlayerRepository
{
    Task<Player?> GetAsync(int pid, CancellationToken ct = default);
    Task<Player?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Player>> SearchAsync(
        PlayerSearchMode mode, string query, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Player>> GetForAccountAsync(int accountId, CancellationToken ct = default);
    Task<IReadOnlyList<Player>> GetLeaderboardAsync(int limit, CancellationToken ct = default);
    Task<(PlayerCreateResult Result, Player? Player)> CreateForAccountAsync(
        int accountId, string name, string normalizedName, string language,
        CancellationToken ct = default);
    Task<PlayerDeleteResult> DeleteForAccountAsync(int accountId, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

public enum PlayerSearchMode
{
    Forward,
    Part,
    Perfect,
}

public enum PlayerCreateResult
{
    Created,
    AlreadyExists,
}

public enum PlayerDeleteResult
{
    Deleted,
    NotFound,
}

public enum RegistrationResult
{
    Created,
    GameIdTaken,
}

public interface IAccountRepository
{
    Task<Account?> GetByGameIdAsync(string gameId, CancellationToken ct = default);

    Task<(RegistrationResult Result, Account? Account)> RegisterAsync(
        string gameId, string passwordHash, string regCode, CancellationToken ct = default);
    Task<bool> ClaimFirstLoginAsync(int accountId, CancellationToken ct = default);
    Task TouchLoginAsync(int accountId, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

public interface ICatalogRepository
{
    Task<IReadOnlyList<InformationItem>> GetInformationAsync(CancellationToken ct = default);
}

public interface IMatchRepository
{
    Task RecordAsync(MatchRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<MatchRecord>> GetRecentAsync(int pid, int limit, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------

public sealed class PlayerRepository(GameDbContext db) : IPlayerRepository
{
    public Task<Player?> GetAsync(int pid, CancellationToken ct = default)
        => db.Players.Include(p => p.Stats).FirstOrDefaultAsync(p => p.Pid == pid, ct);

    public Task<Player?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var normalized = PlayerNamePolicy.Normalize(name);
        return db.Players.Include(p => p.Stats)
            .FirstOrDefaultAsync(p => p.NormalizedName == normalized, ct);
    }

    public async Task<IReadOnlyList<Player>> SearchAsync(
        PlayerSearchMode mode, string query, int limit, CancellationToken ct = default)
    {
        var normalized = PlayerNamePolicy.Normalize(query);
        if (normalized.Length == 0 || limit <= 0)
            return Array.Empty<Player>();

        IQueryable<Player> filtered = mode switch
        {
            PlayerSearchMode.Forward => db.Players.Where(p => p.NormalizedName.StartsWith(normalized)),
            PlayerSearchMode.Part => db.Players.Where(p => p.NormalizedName.Contains(normalized)),
            PlayerSearchMode.Perfect => db.Players.Where(p => p.NormalizedName == normalized),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

        return await filtered
            .Include(p => p.Stats)
            .OrderBy(p => p.NormalizedName)
            .ThenBy(p => p.Pid)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Player>> GetForAccountAsync(int accountId, CancellationToken ct = default)
        => await db.Players.Include(p => p.Stats)
            .Where(p => p.AccountId == accountId)
            .OrderBy(p => p.Pid)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Player>> GetLeaderboardAsync(int limit, CancellationToken ct = default)
        => await db.Players.Include(p => p.Stats)
            .OrderByDescending(p => p.Point)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<(PlayerCreateResult Result, Player? Player)> CreateForAccountAsync(
        int accountId, string name, string normalizedName, string language,
        CancellationToken ct = default)
    {
        if (await db.Players.AnyAsync(
                p => p.AccountId == accountId || p.NormalizedName == normalizedName, ct))
            return (PlayerCreateResult.AlreadyExists, null);

        var player = new Player
        {
            AccountId = accountId,
            Name = name,
            NormalizedName = normalizedName,
            Lang = language,
            Division = "D3C",
            Kind = "NORMAL",
            Rating = 500,
            Manner = 3,
            SelfReportLevel = "PRO",
            PositionWant = "CF",
            AutoMatchWant = true,
            BeginnerMark = true,
            ChatEnabled = true,
            Stats = new PlayerStats(),
        };

        db.Players.Add(player);
        try
        {
            // SaveChanges wraps the Player and PlayerStats inserts in one transaction.
            await db.SaveChangesAsync(ct);
            return (PlayerCreateResult.Created, player);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
        {
            // The unique account/name indexes are authoritative under concurrent creates.
            return (PlayerCreateResult.AlreadyExists, null);
        }
    }

    public async Task<PlayerDeleteResult> DeleteForAccountAsync(
        int accountId, CancellationToken ct = default)
    {
        var player = await db.Players.Include(p => p.Stats)
            .SingleOrDefaultAsync(p => p.AccountId == accountId, ct);
        if (player is null)
            return PlayerDeleteResult.NotFound;

        db.Players.Remove(player);
        await db.SaveChangesAsync(ct);
        return PlayerDeleteResult.Deleted;
    }

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    private static bool IsUniqueConstraint(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.Sqlite.SqliteException
                {
                    SqliteErrorCode: 19,
                    SqliteExtendedErrorCode: 1555 or 2067,
                })
                return true;

            if (current is MySqlConnector.MySqlException { Number: 1062 })
                return true;
        }

        return false;
    }
}

public sealed class AccountRepository(GameDbContext db) : IAccountRepository
{
    public Task<Account?> GetByGameIdAsync(string gameId, CancellationToken ct = default)
        => db.Accounts.FirstOrDefaultAsync(a => a.GameId == gameId, ct);

    public async Task<(RegistrationResult Result, Account? Account)> RegisterAsync(
        string gameId, string passwordHash, string regCode, CancellationToken ct = default)
    {
        gameId = gameId.Trim();
        passwordHash = passwordHash.Trim().ToLowerInvariant();
        regCode = regCode.Trim().ToUpperInvariant();

        if (await GetByGameIdAsync(gameId, ct) is not null)
            return (RegistrationResult.GameIdTaken, null);

        var account = new Account
        {
            GameId = gameId,
            PasswordHash = passwordHash,
            RegCode = regCode,
        };

        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return (RegistrationResult.Created, account);
    }

    public async Task<bool> ClaimFirstLoginAsync(int accountId, CancellationToken ct = default)
        => await db.Accounts
            .Where(a => a.Id == accountId && a.FirstLogin)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(a => a.FirstLogin, false), ct) == 1;

    public async Task TouchLoginAsync(int accountId, CancellationToken ct = default)
    {
        var account = await db.Accounts.FindAsync([accountId], ct);
        if (account is null) return;

        account.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class CatalogRepository(GameDbContext db) : ICatalogRepository
{
    public async Task<IReadOnlyList<InformationItem>> GetInformationAsync(CancellationToken ct = default)
        => await db.Information.Where(i => i.Enabled)
            .OrderByDescending(i => i.PublishedAt)
            .ToListAsync(ct);
}

public sealed class MatchRepository(GameDbContext db) : IMatchRepository
{
    public async Task RecordAsync(MatchRecord record, CancellationToken ct = default)
    {
        db.Matches.Add(record);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MatchRecord>> GetRecentAsync(int pid, int limit, CancellationToken ct = default)
        => await db.Matches
            .Where(m => m.HomePid == pid || m.AwayPid == pid)
            .OrderByDescending(m => m.PlayedAt)
            .Take(limit)
            .ToListAsync(ct);
}
