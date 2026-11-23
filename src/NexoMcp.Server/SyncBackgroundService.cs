using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoMcp.Core.Cache;

namespace NexoMcp.Server;

/// <summary>
/// Runs the startup sync and the periodic re-sync as a proper hosted service. A plain
/// synchronous call in Program.cs before host.RunAsync() used to block the whole process on it -
/// for a first sync pulling three years of history through Sfera, that can be tens of seconds,
/// during which an MCP client's "initialize" handshake goes unanswered and can time out before
/// the server ever gets to start listening. BackgroundService.StartAsync starts ExecuteAsync and
/// returns without waiting for it - but only once ExecuteAsync actually yields, which a fully
/// synchronous method body never does until it returns. The explicit Task.Yield() below makes
/// that true regardless of the order other hosted services happen to be registered in (it was
/// confirmed, by a second review pass, that this only worked before because the MCP SDK's own
/// stdio transport happens to register its hosted service first - a reordering, or a different
/// MCP SDK version, would have silently brought the blocking back).
/// </summary>
public sealed class SyncBackgroundService(NexoConfig config, NexoCache cache, ILogger<SyncBackgroundService> logger) : BackgroundService
{
    // How often a full resync (the whole retained window, not just the narrow incremental one)
    // runs - see NexoCache.GetLastFullSyncUtc's doc comment for why one alone isn't enough.
    private static readonly TimeSpan FullResyncInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        RunSyncBestEffort("startowa");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, config.SyncIntervalMinutes)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            RunSyncBestEffort("okresowa");
        }
    }

    private void RunSyncBestEffort(string label)
    {
        // DateTime? - DateTime? comparisons via lifted operators treat null as "not greater
        // than" (false), not as "always due" - so a never-synced cache (GetLastFullSyncUtc()
        // returns null) must be checked explicitly, or the very first run would wrongly skip
        // the full resync it most needs.
        var lastFullSync = cache.GetLastFullSyncUtc();
        bool fullResyncDue = lastFullSync is null || DateTime.UtcNow - lastFullSync.Value > FullResyncInterval;

        // Best-effort: if nexo/Sfera isn't reachable right now, log it and keep serving whatever
        // is already in the cache rather than taking the whole server down.
        try
        {
            // The 1st of the month, not "exactly 3 years ago today": a mid-month start would
            // leave the series' very first month permanently partial (it never gets re-read from
            // its own actual start, since the next full resync's window starts even later) -
            // skewing a whole-history average and, more visibly, a seasonal per-month average
            // that includes it.
            var fullResyncFrom = DateTime.UtcNow.AddYears(-3);
            var from = fullResyncDue ? new DateOnly(fullResyncFrom.Year, fullResyncFrom.Month, 1) : (DateOnly?)null;
            var result = SyncService.Sync(config, cache, from);
            if (fullResyncDue)
                cache.SetLastFullSyncUtc(DateTime.UtcNow);

            logger.LogInformation(
                "Synchronizacja {Label}{Full}: {Invoices} faktur sprzedazy, {Purchases} faktur zakupu, {CashOps} operacji bankowych/kasowych.",
                label, fullResyncDue ? " (pelna)" : "", result.Invoices, result.PurchaseInvoices, result.CashOperations);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Synchronizacja {Label} nie powiodla sie - serwer dziala na dotychczasowym cache.", label);
        }
    }
}
