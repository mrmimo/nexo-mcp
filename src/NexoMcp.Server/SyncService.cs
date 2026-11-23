using NexoMcp.Core.Cache;
using NexoMcp.Server.Sfera;

namespace NexoMcp.Server;

/// <summary>
/// Pulls invoices from nexo through Sfera into the local SQLite cache, so the MCP tools answer
/// from the cache instead of opening a Sfera connection (and hitting the live nexo database) on
/// every single tool call.
/// </summary>
public static class SyncService
{
    public static int Sync(NexoConfig config, InvoiceCache cache, DateOnly? from = null)
    {
        // Re-pull a little before the last sync, not just strictly after it, in case an invoice
        // was edited (e.g. a correction attached) after it was first cached.
        DateOnly effectiveFrom = from
            ?? DateOnly.FromDateTime(cache.GetLastSyncUtc()?.AddDays(-7) ?? DateTime.UtcNow.AddYears(-3));

        InsERT.Moria.Sfera.Uchwyt sfera = SferaConnectionFactory.Connect(config);
        try
        {
            var invoices = SferaInvoiceReader.ReadInvoices(sfera, effectiveFrom, to: DateOnly.FromDateTime(DateTime.Today));
            cache.UpsertInvoices(invoices);
            cache.SetLastSyncUtc(DateTime.UtcNow);
            return invoices.Count;
        }
        finally
        {
            sfera.Dispose();
        }
    }
}
