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
    public static SyncResult Sync(NexoConfig config, NexoCache cache, DateOnly? from = null)
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
            cache.UpsertInvoices(invoices);

            var purchaseInvoices = SferaInvoiceReader.ReadPurchaseInvoices(sfera, effectiveFrom, today);
            cache.UpsertInvoices(purchaseInvoices);

            var bankOperations = SferaCashFlowReader.ReadBankOperations(sfera, effectiveFrom, today);
            var cashOperations = SferaCashFlowReader.ReadCashOperations(sfera, effectiveFrom, today);
            cache.UpsertCashOperations(bankOperations.Concat(cashOperations));

            cache.SetLastSyncUtc(DateTime.UtcNow);
            return new SyncResult(invoices.Count, purchaseInvoices.Count, bankOperations.Count + cashOperations.Count);
        }
        finally
        {
            sfera.Dispose();
        }
    }
}
