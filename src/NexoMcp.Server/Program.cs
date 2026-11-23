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

// A relative NEXO_MCP_CACHE_PATH must not be resolved against the process's current working
// directory: an MCP client is not guaranteed to launch this server from any particular folder
// (the exact problem already fixed for katalogBinariow in SferaConnectionFactory - a relative
// cache path could otherwise land next to wherever the client happens to be, or fail outright).
string cacheDbPath = Path.IsPathRooted(config.CacheDbPath)
    ? config.CacheDbPath
    : Path.Combine(AppContext.BaseDirectory, config.CacheDbPath);
Directory.CreateDirectory(Path.GetDirectoryName(cacheDbPath)!);
var cache = new NexoCache($"Data Source={cacheDbPath}");

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

// Runs the startup and periodic sync without blocking the MCP transport from coming up - see
// SyncBackgroundService's own doc comment for why this used to time out an MCP client's
// "initialize" handshake.
builder.Services.AddHostedService<SyncBackgroundService>();

var host = builder.Build();

await host.RunAsync();
