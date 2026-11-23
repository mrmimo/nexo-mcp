using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using NexoMcp.Core.Cache;
using NexoMcp.Server;
using NexoMcp.Server.Sfera;
using NexoMcp.Server.Tools;

NexoSdkAssemblyResolver.EnsureRegistered();

var config = NexoConfig.FromEnvironment();

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.CacheDbPath))!);
var cache = new NexoCache($"Data Source={config.CacheDbPath}");

var builder = Host.CreateApplicationBuilder(args);

// stdio carries the MCP JSON-RPC protocol on stdout - logs must never go there.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(cache);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<InvoiceTools>()
    .WithTools<AnalyticsTools>()
    .WithTools<SyncTools>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();

// Best-effort startup sync: if nexo/Sfera isn't reachable right now, log it and keep serving
// whatever is already in the cache rather than failing to start.
try
{
    var result = SyncService.Sync(config, cache);
    logger.LogInformation(
        "Synchronizacja startowa: {Invoices} faktur sprzedazy, {Purchases} faktur zakupu, {CashOps} operacji bankowych/kasowych.",
        result.Invoices, result.PurchaseInvoices, result.CashOperations);
}
catch (Exception ex)
{
    logger.LogWarning(ex, "Synchronizacja startowa nie powiodla sie - dziala na dotychczasowym cache.");
}

_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, config.SyncIntervalMinutes)));
        try
        {
            var result = SyncService.Sync(config, cache);
            logger.LogInformation(
                "Synchronizacja okresowa: {Invoices} faktur sprzedazy, {Purchases} faktur zakupu, {CashOps} operacji bankowych/kasowych.",
                result.Invoices, result.PurchaseInvoices, result.CashOperations);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Synchronizacja okresowa nie powiodla sie.");
        }
    }
});

await host.RunAsync();
