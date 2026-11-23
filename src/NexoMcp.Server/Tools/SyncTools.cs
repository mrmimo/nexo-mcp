using System.ComponentModel;
using ModelContextProtocol.Server;
using NexoMcp.Core.Cache;

namespace NexoMcp.Server.Tools;

[McpServerToolType]
public sealed class SyncTools(InvoiceCache cache, NexoConfig config)
{
    [McpServerTool(Name = "sync_invoices"), Description(
        "Pulls the latest invoices from nexo (via Sfera) into the local cache that every other tool " +
        "reads from. Runs automatically on startup and on a timer (see NEXO_MCP_SYNC_INTERVAL_MINUTES) " +
        "- call this directly only when you need fresher data right now.")]
    public object SyncInvoices(
        [Description("ISO date (yyyy-MM-dd) to sync from. Omit to sync since the last sync (with a 7-day overlap).")] string? from = null)
    {
        int count = SyncService.Sync(config, cache, from is null ? null : DateOnly.Parse(from));
        return new { SyncedInvoiceCount = count, SyncedAtUtc = DateTime.UtcNow.ToString("O") };
    }

    [McpServerTool(Name = "get_sync_status"), Description("Reports when the local invoice cache was last synced from nexo.")]
    public object GetSyncStatus()
    {
        return new { LastSyncUtc = cache.GetLastSyncUtc()?.ToString("O") };
    }
}
