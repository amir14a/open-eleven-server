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

                var decided = hub.PublishDecideGameSnapshots();
                if (decided > 0)
                    log.LogDebug("Refreshed {Count} decide-game snapshots", decided);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
