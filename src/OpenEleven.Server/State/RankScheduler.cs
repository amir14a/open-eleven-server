// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>
/// Closes weeks and seasons on time: checks once a minute whether the UTC week or month
/// changed since the last run, so a restart across midnight still closes it.
/// </summary>
public sealed class RankScheduler(
    IServiceScopeFactory scopes,
    IOptionsMonitor<ServerOptions> options,
    TimeProvider time,
    ILogger<RankScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!RankSystem.Applies(options.CurrentValue.GameProfile))
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
                var now = time.GetUtcNow().UtcDateTime;
                await RankSystem.InitializeAsync(db, options.CurrentValue.Rank, now);
                await RankSystem.RolloverAsync(db, options.CurrentValue.Rank, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Rank week/season rollover failed; retrying in a minute");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
