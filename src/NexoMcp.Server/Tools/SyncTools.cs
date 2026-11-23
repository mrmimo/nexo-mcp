using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using NexoMcp.Core.Cache;

namespace NexoMcp.Server.Tools;

[McpServerToolType]
public sealed class SyncTools(NexoCache cache, NexoConfig config)
{
    [McpServerTool(Name = "sync_invoices"), Description(
        "Pobiera z nexo (przez Sfere) najnowsze faktury (sprzedazy i zakupu) oraz operacje " +
        "bankowo-kasowe do lokalnego cache, z ktorego czytaja wszystkie inne narzedzia. Dzieje sie " +
        "to automatycznie przy starcie i okresowo (zobacz NEXO_MCP_SYNC_INTERVAL_MINUTES) - wywolaj " +
        "to narzedzie wprost tylko wtedy, gdy potrzebujesz swiezszych danych juz teraz.")]
    public object SyncInvoices(
        [Description("Data ISO (rrrr-MM-dd), od ktorej synchronizowac. Pomin, aby synchronizowac od ostatniej synchronizacji (z 7-dniowym zapasem).")] string? from = null)
    {
        DateOnly? parsedFrom = null;
        if (from is not null)
        {
            if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                throw new ArgumentException($"from musi byc data w formacie rrrr-MM-dd, otrzymano: \"{from}\".");
            parsedFrom = d;
        }

        var result = SyncService.Sync(config, cache, parsedFrom);
        return new
        {
            SyncedInvoiceCount = result.Invoices,
            SyncedPurchaseInvoiceCount = result.PurchaseInvoices,
            SyncedCashOperationCount = result.CashOperations,
            SyncedAtUtc = DateTime.UtcNow.ToString("O"),
        };
    }

    [McpServerTool(Name = "get_sync_status"), Description("Kiedy lokalny cache byl ostatnio synchronizowany z nexo.")]
    public object GetSyncStatus()
    {
        return new { LastSyncUtc = cache.GetLastSyncUtc()?.ToString("O") };
    }
}
