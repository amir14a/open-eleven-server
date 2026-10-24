// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenEleven.Data.Entities;

namespace OpenEleven.Data;

/// <summary>
/// Creates missing schema, upgrades databases made by older emulator builds, and seeds
/// catalog data. Player profiles are user-owned and are never seeded in production.
/// </summary>
public sealed class DatabaseInitializer(GameDbContext db, ILogger<DatabaseInitializer> log)
{
    /// <summary>desired_position flags used by the old reference-only demo profile.</summary>
    public const int LegacyDesiredPositionMask = (1 << 0) | (1 << 3) | (1 << 7);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        await EnsureAccountSchemaAsync(ct);
        await EnsurePlayerColumnsAsync(ct);
        await EnsureModelSchemaAsync(ct);
        await RemoveLegacyDemoPlayerAsync(ct);
        await BackfillAndValidatePlayerNamesAsync(ct);
        await EnsurePlayerIndexesAsync(ct);
    }

    /// <summary>
    /// Upgrades databases created before the current account authentication fields.
    /// EnsureCreated intentionally does not alter an existing schema.
    /// </summary>
    private async Task EnsureAccountSchemaAsync(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
            await connection.OpenAsync(ct);

        try
        {
            var provider = db.Database.ProviderName ?? "";
            if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                await EnsureSqliteAccountSchemaAsync(connection, ct);
            else if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase))
                await EnsureMySqlAccountSchemaAsync(connection, ct);
        }
        finally
        {
            if (closeWhenDone)
                await connection.CloseAsync();
        }
    }

    private static async Task EnsureSqliteAccountSchemaAsync(
        DbConnection connection, CancellationToken ct)
    {
        var columns = await ReadSqliteColumnsAsync(connection, "Accounts", ct);

        if (!columns.Contains("PasswordHash"))
            await ExecuteAsync(
                connection,
                "ALTER TABLE \"Accounts\" ADD COLUMN \"PasswordHash\" TEXT NOT NULL DEFAULT '';",
                ct);

        if (!columns.Contains("RegCode"))
            await ExecuteAsync(
                connection,
                "ALTER TABLE \"Accounts\" ADD COLUMN \"RegCode\" TEXT NULL;",
                ct);

        if (!columns.Contains("FirstLogin"))
            await ExecuteAsync(
                connection,
                "ALTER TABLE \"Accounts\" ADD COLUMN \"FirstLogin\" INTEGER NOT NULL DEFAULT 1;",
                ct);

        // Rebuild the old unique index as a normal lookup index. This is idempotent.
        await ExecuteAsync(connection, "DROP INDEX IF EXISTS \"IX_Accounts_RegCode\";", ct);
        await ExecuteAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS \"IX_Accounts_RegCode\" ON \"Accounts\" (\"RegCode\");",
            ct);
    }

    private static async Task EnsureMySqlAccountSchemaAsync(
        DbConnection connection, CancellationToken ct)
    {
        var columns = await ReadMySqlColumnsAsync(connection, "Accounts", ct);

        if (!columns.Contains("PasswordHash"))
            await ExecuteAsync(
                connection,
                "ALTER TABLE `Accounts` ADD COLUMN `PasswordHash` varchar(255) NOT NULL DEFAULT '';",
                ct);

        if (!columns.Contains("RegCode"))
            await ExecuteAsync(
                connection,
                "ALTER TABLE `Accounts` ADD COLUMN `RegCode` varchar(64) NULL;",
                ct);

        if (!columns.Contains("FirstLogin"))
            await ExecuteAsync(
                connection,
                "ALTER TABLE `Accounts` ADD COLUMN `FirstLogin` tinyint(1) NOT NULL DEFAULT 1;",
                ct);

        var nonUnique = await MySqlIndexNonUniqueAsync(connection, "Accounts", "IX_Accounts_RegCode", ct);
        if (nonUnique == 0)
        {
            await ExecuteAsync(connection, "DROP INDEX `IX_Accounts_RegCode` ON `Accounts`;", ct);
            nonUnique = null;
        }

        if (nonUnique is null)
            await ExecuteAsync(
                connection,
                "CREATE INDEX `IX_Accounts_RegCode` ON `Accounts` (`RegCode`);",
                ct);
    }

    private async Task EnsurePlayerColumnsAsync(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
            await connection.OpenAsync(ct);

        try
        {
            var provider = db.Database.ProviderName ?? "";
            if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                var columns = await ReadSqliteColumnsAsync(connection, "Players", ct);
                if (!columns.Contains("NormalizedName"))
                    await ExecuteAsync(
                        connection,
                        "ALTER TABLE \"Players\" ADD COLUMN \"NormalizedName\" TEXT NOT NULL DEFAULT '';",
                        ct);
                if (!columns.Contains("QuickMatchOptions"))
                    await ExecuteAsync(connection, "ALTER TABLE \"Players\" ADD COLUMN \"QuickMatchOptions\" TEXT NOT NULL DEFAULT '';", ct);
                if (!columns.Contains("CompetitionId"))
                    await ExecuteAsync(connection, "ALTER TABLE \"Players\" ADD COLUMN \"CompetitionId\" INTEGER NOT NULL DEFAULT 0;", ct);

                // Matches written before friendlies were stored were all ranking matches.
                var matchColumns = await ReadSqliteColumnsAsync(connection, "Matches", ct);
                if (matchColumns.Count > 0 && !matchColumns.Contains("Ranked"))
                    await ExecuteAsync(connection, "ALTER TABLE \"Matches\" ADD COLUMN \"Ranked\" INTEGER NOT NULL DEFAULT 1;", ct);
                if (matchColumns.Count > 0 && !matchColumns.Contains("Settings"))
                    await ExecuteAsync(connection, "ALTER TABLE \"Matches\" ADD COLUMN \"Settings\" TEXT NOT NULL DEFAULT '';", ct);
            }
            else if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase))
            {
                var columns = await ReadMySqlColumnsAsync(connection, "Players", ct);
                if (!columns.Contains("NormalizedName"))
                    await ExecuteAsync(
                        connection,
                        "ALTER TABLE `Players` ADD COLUMN `NormalizedName` varchar(64) NOT NULL DEFAULT '';",
                        ct);
                if (!columns.Contains("QuickMatchOptions"))
                    await ExecuteAsync(connection, "ALTER TABLE `Players` ADD COLUMN `QuickMatchOptions` varchar(64) NOT NULL DEFAULT '';", ct);
                if (!columns.Contains("CompetitionId"))
                    await ExecuteAsync(connection, "ALTER TABLE `Players` ADD COLUMN `CompetitionId` int NOT NULL DEFAULT 0;", ct);

                var matchColumns = await ReadMySqlColumnsAsync(connection, "Matches", ct);
                if (matchColumns.Count > 0 && !matchColumns.Contains("Ranked"))
                    await ExecuteAsync(connection, "ALTER TABLE `Matches` ADD COLUMN `Ranked` tinyint(1) NOT NULL DEFAULT 1;", ct);
                if (matchColumns.Count > 0 && !matchColumns.Contains("Settings"))
                    await ExecuteAsync(connection, "ALTER TABLE `Matches` ADD COLUMN `Settings` longtext NOT NULL;", ct);
            }
        }
        finally
        {
            if (closeWhenDone)
                await connection.CloseAsync();
        }
    }

    /// <summary>Columns added to existing rows with a value other than the type's zero.</summary>
    private static readonly Dictionary<(string Table, string Column), string> AddedColumnDefaults = new()
    {
        [("Matches", "Completed")] = "1",
    };

    /// <summary>
    /// Brings an existing database up to the current model: creates the tables it lacks
    /// (with their indexes, from EF's own create script) and adds missing columns. New
    /// entities and properties therefore need no hand-written upgrade step.
    /// </summary>
    private async Task EnsureModelSchemaAsync(CancellationToken ct)
    {
        var provider = db.Database.ProviderName ?? "";
        var sqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        if (!sqlite && !provider.Contains("MySql", StringComparison.OrdinalIgnoreCase))
            return;

        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
            await connection.OpenAsync(ct);

        try
        {
            var q = sqlite ? "\"" : "`";
            var statements = System.Text.RegularExpressions.Regex
                .Split(db.Database.GenerateCreateScript(), @";\s*(?:\r?\n|$)")
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();

            foreach (var entity in db.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (table is null)
                    continue;

                var columns = sqlite
                    ? await ReadSqliteColumnsAsync(connection, table, ct)
                    : await ReadMySqlColumnsAsync(connection, table, ct);

                if (columns.Count == 0)
                {
                    foreach (var sql in statements.Where(s =>
                                 s.StartsWith($"CREATE TABLE {q}{table}{q}", StringComparison.Ordinal)
                                 || (s.StartsWith("CREATE ", StringComparison.Ordinal)
                                     && s.Contains($" ON {q}{table}{q}", StringComparison.Ordinal))))
                        await ExecuteAsync(connection, sql + ";", ct);
                    log.LogInformation("Created table {Table}.", table);
                    continue;
                }

                var storeObject = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, null);
                foreach (var property in entity.GetProperties())
                {
                    var column = property.GetColumnName(storeObject);
                    if (column is null || columns.Contains(column))
                        continue;

                    var type = property.GetColumnType();
                    var text = type.Contains("text", StringComparison.OrdinalIgnoreCase)
                               || type.Contains("char", StringComparison.OrdinalIgnoreCase);
                    var nullable = property.IsNullable ? "NULL" : "NOT NULL";
                    var fallback = text ? "''" : "0";
                    var value = AddedColumnDefaults.GetValueOrDefault((table, column), fallback);
                    // MySQL refuses a DEFAULT on longtext; NOT NULL alone fills existing rows with ''.
                    var defaultClause = property.IsNullable || (!sqlite && type.Contains("text", StringComparison.OrdinalIgnoreCase))
                        ? ""
                        : $" DEFAULT {value}";

                    await ExecuteAsync(connection,
                        $"ALTER TABLE {q}{table}{q} ADD COLUMN {q}{column}{q} {type} {nullable}{defaultClause};", ct);
                    log.LogInformation("Added column {Table}.{Column}.", table, column);
                }
            }
        }
        finally
        {
            if (closeWhenDone)
                await connection.CloseAsync();
        }
    }

    private async Task BackfillAndValidatePlayerNamesAsync(CancellationToken ct)
    {
        var allPlayers = await db.Players.OrderBy(p => p.Pid).ToListAsync(ct);
        var accountConflict = allPlayers.GroupBy(p => p.AccountId).FirstOrDefault(g => g.Count() > 1);
        if (accountConflict is not null)
            throw new InvalidOperationException(
                $"Account {accountConflict.Key} owns multiple player profiles " +
                $"({string.Join(", ", accountConflict.Select(p => p.Pid))}). Resolve this before startup.");

        foreach (var player in allPlayers)
            player.NormalizedName = PlayerNamePolicy.Normalize(player.Name);

        var nameConflict = allPlayers
            .GroupBy(p => p.NormalizedName, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (nameConflict is not null)
            throw new InvalidOperationException(
                $"Player names are duplicated case-insensitively: " +
                $"{string.Join(", ", nameConflict.Select(p => $"{p.Pid}:{p.Name}"))}. Resolve this before startup.");

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }

    private async Task EnsurePlayerIndexesAsync(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
            await connection.OpenAsync(ct);

        try
        {
            var provider = db.Database.ProviderName ?? "";
            if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                // Older EF schemas created a non-unique FK index under this name.
                await ExecuteAsync(connection, "DROP INDEX IF EXISTS \"IX_Players_AccountId\";", ct);
                await ExecuteAsync(
                    connection,
                    "CREATE UNIQUE INDEX \"IX_Players_AccountId\" ON \"Players\" (\"AccountId\");",
                    ct);
                await ExecuteAsync(
                    connection,
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Players_NormalizedName\" " +
                    "ON \"Players\" (\"NormalizedName\");",
                    ct);
            }
            else if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase))
            {
                await EnsureMySqlUniqueIndexAsync(
                    connection, "Players", "IX_Players_AccountId", "AccountId", ct);
                await EnsureMySqlUniqueIndexAsync(
                    connection, "Players", "IX_Players_NormalizedName", "NormalizedName", ct);
            }
        }
        finally
        {
            if (closeWhenDone)
                await connection.CloseAsync();
        }
    }

    private static async Task EnsureMySqlUniqueIndexAsync(
        DbConnection connection, string table, string index, string column, CancellationToken ct)
    {
        var nonUnique = await MySqlIndexNonUniqueAsync(connection, table, index, ct);
        if (nonUnique == 1)
        {
            await ExecuteAsync(connection, $"DROP INDEX `{index}` ON `{table}`;", ct);
            nonUnique = null;
        }

        if (nonUnique is null)
            await ExecuteAsync(
                connection,
                $"CREATE UNIQUE INDEX `{index}` ON `{table}` (`{column}`);",
                ct);
    }

    private async Task RemoveLegacyDemoPlayerAsync(CancellationToken ct)
    {
        var account = await db.Accounts
            .Include(a => a.Players)
            .ThenInclude(p => p.Stats)
            .SingleOrDefaultAsync(a => a.GameId == "local", ct);

        if (account is null || !IsExactLegacyDemo(account))
            return;

        var pid = account.Players[0].Pid;
        db.Accounts.Remove(account);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Removed untouched legacy demo account local and player pid {Pid}.", pid);
    }

    private static bool IsExactLegacyDemo(Account account)
    {
        if (account.PasswordHash != ""
            || account.RegCode is not null
            || !account.FirstLogin
            || account.Banned
            || account.BanReason is not null
            || account.LastLoginAt is not null
            || account.Players.Count != 1)
            return false;

        var p = account.Players[0];
        var s = p.Stats;
        return p.Name == "Local Player"
               && p.Division == "D3C"
               && p.Kind == "NORMAL"
               && p.Lang == "EN"
               && p.Intro == "PLAYERINFO FIELD TEST"
               && p.Rating == 742
               && p.Point == 12345
               && p.Rank == 321
               && p.Manner == 3
               && p.Country == 50
               && p.Area == 0
               && p.BirthMonth == 9
               && p.BirthDay == 12
               && p.FavoriteTeam == 5
               && p.FavoritePlayer == 4618
               && p.SelfReportLevel == "PRO"
               && p.PositionWant == "CF"
               && p.DesiredPositionMask == LegacyDesiredPositionMask
               && p.AutoMatchWant
               && p.BeginnerMark
               && p.ChatEnabled
               && s.MatchCount == 37
               && s.WinCount == 21
               && s.LoseCount == 9
               && s.DrawCount == 7
               && s.ContinuousWins == 5
               && s.MaxContinuousWins == 8
               && s.DisconnectCount == 2
               && s.DisconnectWins == 1
               && s.DisconnectLosses == 1
               && s.Goals == 84
               && s.GoalsAgainst == 42
               && s.TotalCombination == 18
               && s.MaxCombination == 4;
    }

    private static async Task<HashSet<string>> ReadSqliteColumnsAsync(
        DbConnection connection, string table, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<HashSet<string>> ReadMySqlColumnsAsync(
        DbConnection connection, string table, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table;";
        AddParameter(command, "@table", table);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<int?> MySqlIndexNonUniqueAsync(
        DbConnection connection, string table, string index, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT NON_UNIQUE FROM INFORMATION_SCHEMA.STATISTICS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table " +
            "AND INDEX_NAME = @index LIMIT 1;";
        AddParameter(command, "@table", table);
        AddParameter(command, "@index", index);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task ExecuteAsync(
        DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
