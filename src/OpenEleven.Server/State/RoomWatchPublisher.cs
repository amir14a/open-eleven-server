// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenEleven.Server.State;

public sealed class RoomWatchPublisher(
    Hub hub,
    ILogger<RoomWatchPublisher> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var stateSent = hub.PublishRoomStateSnapshots();
                if (stateSent > 0)
                    log.LogDebug("Refreshed {Count} CMD_WATCH_ROOMSTATE snapshots", stateSent);

                var sent = hub.PublishStartableGameEntrySnapshots();
                if (sent > 0)
                    log.LogDebug("Refreshed {Count} CMD_WATCH_ENTRY_GAME snapshots", sent);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
