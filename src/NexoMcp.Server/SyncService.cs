using NexoMcp.Core.Cache;
using NexoMcp.Server.Sfera;

namespace NexoMcp.Server;

public sealed record SyncResult(int Invoices, int PurchaseInvoices, int CashOperations);

/// <summary>
/// Pulls invoices (sales and purchase) and bank/cash operations from nexo through Sfera into the
/// local SQLite cache, so the MCP tools answer from the cache instead of opening a Sfera
/// connection (and hitting the live nexo database) on every single tool call.
/// </summary>
public static class SyncService
{
    // Guards against the startup sync and the periodic background sync (or two overlapping
    // sync_invoices tool calls) running at the same time - each opens its own Sfera session, so
    // without this two could run concurrently, duplicating load on nexo for no benefit (the
    // second one re-reads exactly what the first is already reading).
    private static readonly SemaphoreSlim SyncLock = new(1, 1);

    public static SyncResult Sync(NexoConfig config, NexoCache cache, DateOnly? from = null)
    {
        if (!SyncLock.Wait(0))
            throw new InvalidOperationException("Synchronizacja jest juz w toku - poczekaj, az sie zakonczy.");

        try
        {
            return SyncCore(config, cache, from);
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private static SyncResult SyncCore(NexoConfig config, NexoCache cache, DateOnly? from)
    {
        // Re-pull a little before the last sync, not just strictly after it, in case a document
        // or operation was edited (e.g. a correction attached) after it was first cached.
        DateOnly effectiveFrom = from
            ?? DateOnly.FromDateTime(cache.GetLastSyncUtc()?.AddDays(-7) ?? DateTime.UtcNow.AddYears(-3));
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);

        InsERT.Moria.Sfera.Uchwyt sfera = SferaConnectionFactory.Connect(config);
        try
        {
            var invoices = SferaInvoiceReader.ReadInvoices(sfera, effectiveFrom, today);
            var purchaseInvoices = SferaInvoiceReader.ReadPurchaseInvoices(sfera, effectiveFrom, today);
            var bankOperations = SferaCashFlowReader.ReadBankOperations(sfera, effectiveFrom, today);
            var cashOperations = SferaCashFlowReader.ReadCashOperations(sfera, effectiveFrom, today);

            // Replaces the whole synced window in one transaction: upsert alone only adds or
            // updates rows, so a document or operation deleted/voided in nexo since the last
            // sync would otherwise linger in the cache forever - and deleting/upserting as
            // separate auto-committed calls could leave the window empty if something throws
            // partway through (see NexoCache.ReplaceWindow).
            cache.ReplaceWindow(effectiveFrom, today, invoices, purchaseInvoices, bankOperations.Concat(cashOperations));

            cache.SetLastSyncUtc(DateTime.UtcNow);
            return new SyncResult(invoices.Count, purchaseInvoices.Count, bankOperations.Count + cashOperations.Count);
        }
        finally
        {
            sfera.Dispose();
        }
    }
}
