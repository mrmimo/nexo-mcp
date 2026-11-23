using InsERT.Mox.Product;

namespace NexoMcp.Server;

/// <summary>
/// All configuration comes from environment variables - nothing here is ever committed.
/// Copy .env.example to .env (or set these in your MCP client's server config) and fill it in
/// with your own nexo connection details.
/// </summary>
public sealed class NexoConfig
{
    public required string Server { get; init; }
    public required string Database { get; init; }

    /// <summary>Governs only the SQL Server connection layer. Logging into Sfera as a nexo operator (<see cref="Login"/>/<see cref="Password"/>) is a separate, always-required step on top of this.</summary>
    public bool WindowsAuthentication { get; init; } = true;

    /// <summary>SQL Server login - only used (and only required) when <see cref="WindowsAuthentication"/> is false.</summary>
    public string? SqlLogin { get; init; }
    public string? SqlPassword { get; init; }

    /// <summary>nexo operator login - always required, independent of <see cref="WindowsAuthentication"/>.</summary>
    public required string Login { get; init; }
    public string? Password { get; init; }

    /// <summary>
    /// Which InsERT product license to connect as - affects which modules/menus Sfera exposes,
    /// not whether sales invoices can be read. Common values: "Subiekt", "Gestor". See your
    /// nexo SDK's ProductId enum for the full list. Parsed eagerly here (not only when a sync
    /// first runs) so a typo fails loudly at startup instead of as a logged warning later.
    /// </summary>
    public required ProductId ProductId { get; init; }

    public string CacheDbPath { get; init; } = "cache/invoices.db";
    public int SyncIntervalMinutes { get; init; } = 60;

    public static NexoConfig FromEnvironment()
    {
        string? server = Environment.GetEnvironmentVariable("NEXO_SERVER");
        string? database = Environment.GetEnvironmentVariable("NEXO_DATABASE");
        string? login = Environment.GetEnvironmentVariable("NEXO_LOGIN");
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(login))
        {
            throw new InvalidOperationException(
                "NEXO_SERVER, NEXO_DATABASE i NEXO_LOGIN sa wymagane (patrz .env.example). " +
                "NEXO_SERVER to instancja SQL Server z Twojej instalacji nexo (np. \"(local)\\\\INSERTNEXO\"), " +
                "NEXO_DATABASE to nazwa bazy firmowej, NEXO_LOGIN to login OPERATORA nexo (niezaleznie od " +
                "NEXO_WINDOWS_AUTH, ktory dotyczy tylko polaczenia z SQL Serverem).");
        }

        bool windowsAuth = !bool.TryParse(Environment.GetEnvironmentVariable("NEXO_WINDOWS_AUTH"), out var parsed) || parsed;

        string? sqlLogin = Environment.GetEnvironmentVariable("NEXO_SQL_LOGIN");
        if (!windowsAuth && string.IsNullOrWhiteSpace(sqlLogin))
        {
            throw new InvalidOperationException(
                "NEXO_SQL_LOGIN jest wymagany, gdy NEXO_WINDOWS_AUTH=false (login do SQL Servera, " +
                "inny niz NEXO_LOGIN, ktory jest loginem operatora nexo).");
        }

        string productIdRaw = Environment.GetEnvironmentVariable("NEXO_PRODUCT_ID") ?? "Subiekt";
        if (!Enum.TryParse<ProductId>(productIdRaw, ignoreCase: true, out var productId))
        {
            throw new InvalidOperationException(
                $"NEXO_PRODUCT_ID=\"{productIdRaw}\" nie jest rozpoznawanym produktem InsERT (np. Subiekt, Gestor). " +
                "Sprawdz liste wartosci w ProductId z Twojego SDK nexo.");
        }

        return new NexoConfig
        {
            Server = server,
            Database = database,
            WindowsAuthentication = windowsAuth,
            SqlLogin = sqlLogin,
            SqlPassword = Environment.GetEnvironmentVariable("NEXO_SQL_PASSWORD"),
            Login = login,
            Password = Environment.GetEnvironmentVariable("NEXO_PASSWORD"),
            ProductId = productId,
            CacheDbPath = Environment.GetEnvironmentVariable("NEXO_MCP_CACHE_PATH") ?? "cache/invoices.db",
            SyncIntervalMinutes = int.TryParse(Environment.GetEnvironmentVariable("NEXO_MCP_SYNC_INTERVAL_MINUTES"), out var minutes)
                ? minutes
                : 60,
        };
    }
}
