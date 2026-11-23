using Microsoft.Data.Sqlite;
using NexoMcp.Core.Models;

namespace NexoMcp.Core.Cache;

/// <summary>
/// Local SQLite mirror of invoices (sales and purchase) and cash/bank operations read from nexo.
/// Analytics run against this cache instead of re-querying the live nexo database (and the Sfera
/// API) on every request.
///
/// Opens a new <see cref="SqliteConnection"/> per call rather than holding one connection open
/// for the cache's lifetime. The earlier single-shared-connection design meant a long-running
/// sync's transaction (UpsertInvoices against years of history) left the one connection "busy",
/// so any MCP tool call landing on the same connection while a sync was in flight threw
/// ("Execute requires the command to have a transaction object when the connection assigned to
/// the command is in a pending local transaction" or a SQLite busy error) - not a hypothetical
/// race, a real one on an hourly background sync. A file-backed database additionally gets WAL
/// mode, which lets readers keep working while a writer holds a transaction open.
/// </summary>
public sealed class NexoCache : IDisposable
{
    // Bump this whenever EnsureSchema's CREATE TABLE statements change shape (a new/renamed
    // column, a new table). The cache is disposable by design (see README) - rebuilding it from
    // nexo is always safe - so a mismatch here drops and recreates every table instead of a
    // user's existing cache failing every single call with "no such column" after an upgrade.
    private const int SchemaVersion = 1;

    private readonly string _connectionString;

    // For ":memory:" databases (always used in tests, via "Data Source=:memory:;Cache=Shared")
    // SQLite's shared in-memory database only exists while at least one connection to it is
    // open - opening and closing a connection per call would otherwise create a *new*, empty
    // database on every single operation. This connection is never used for queries; it exists
    // purely to keep the shared in-memory database alive for the cache's lifetime. It is null
    // for a real file-backed database, which needs no such thing.
    private readonly SqliteConnection? _keepAlive;

    /// <summary>
    /// <paramref name="connectionString"/> is a full Microsoft.Data.Sqlite connection string,
    /// e.g. "Data Source=cache/invoices.db" for a file or "Data Source=:memory:;Cache=Shared"
    /// for a throwaway in-memory database (tests).
    /// </summary>
    public NexoCache(string connectionString)
    {
        _connectionString = connectionString;

        if (connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            _keepAlive = new SqliteConnection(connectionString);
            _keepAlive.Open();
            using var pragma = _keepAlive.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }

        using var pooled = Open();
        EnsureSchema(pooled.Connection);
    }

    /// <summary>
    /// For the in-memory case, hands back the one kept-alive connection wrapped so that disposing
    /// it here is a no-op - every call site below uses "using var pooled = Open();" exactly as if
    /// it owned a private connection, without needing its own in-memory special case.
    /// </summary>
    private PooledConnection Open()
    {
        if (_keepAlive is not null)
            return new PooledConnection(_keepAlive, ownsIt: false);

        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return new PooledConnection(connection, ownsIt: true);
    }

    private readonly struct PooledConnection(SqliteConnection connection, bool ownsIt) : IDisposable
    {
        public SqliteConnection Connection => connection;
        public void Dispose()
        {
            if (ownsIt)
                connection.Dispose();
        }
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using (var versionCmd = connection.CreateCommand())
        {
            versionCmd.CommandText = "PRAGMA user_version;";
            long onDiskVersion = (long)versionCmd.ExecuteScalar()!;

            // A cache built before this versioning mechanism existed (or by a version of this
            // project that predates it) reads back as version 0 - there is no "nothing on disk
            // yet" case to distinguish it from, because a freshly created database ALSO starts at
            // user_version 0. The two are told apart by whether the invoices table already
            // exists: if it does, this is an old, un-versioned cache - not a brand new one - and
            // it must be dropped exactly like any other version mismatch below, or
            // "CREATE TABLE IF NOT EXISTS" keeps its old (possibly column-incompatible) shape
            // forever while PRAGMA user_version is stamped to SchemaVersion regardless, which
            // would permanently hide the mismatch on every later run.
            bool hasOldUnversionedTable = false;
            if (onDiskVersion == 0)
            {
                using var existsCmd = connection.CreateCommand();
                existsCmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'invoices';";
                hasOldUnversionedTable = existsCmd.ExecuteScalar() is not null;
            }

            if ((onDiskVersion != 0 && onDiskVersion != SchemaVersion) || hasOldUnversionedTable)
            {
                using var dropCmd = connection.CreateCommand();
                dropCmd.CommandText = """
                    DROP TABLE IF EXISTS invoices;
                    DROP TABLE IF EXISTS invoice_lines;
                    DROP TABLE IF EXISTS cash_operations;
                    DROP TABLE IF EXISTS sync_state;
                    """;
                dropCmd.ExecuteNonQuery();
            }
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS invoices (
                id INTEGER PRIMARY KEY,
                full_number TEXT NOT NULL,
                issue_date TEXT NOT NULL,
                due_date TEXT NULL,
                kind TEXT NOT NULL,
                direction TEXT NOT NULL DEFAULT 'Sale',
                client_symbol TEXT NOT NULL,
                client_name TEXT NOT NULL,
                client_tax_id TEXT NULL,
                currency TEXT NOT NULL,
                net_total REAL NOT NULL,
                gross_total REAL NOT NULL,
                vat_total REAL NOT NULL,
                correction_of_id INTEGER NULL,
                notes TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS invoice_lines (
                invoice_id INTEGER NOT NULL,
                line_no INTEGER NOT NULL,
                product_symbol TEXT NOT NULL,
                description TEXT NOT NULL,
                quantity REAL NOT NULL,
                unit TEXT NOT NULL,
                unit_net_price REAL NOT NULL,
                vat_rate REAL NOT NULL,
                net_value REAL NOT NULL,
                gross_value REAL NOT NULL,
                PRIMARY KEY (invoice_id, line_no),
                FOREIGN KEY (invoice_id) REFERENCES invoices (id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_invoices_issue_date ON invoices (issue_date);
            CREATE INDEX IF NOT EXISTS ix_invoices_client ON invoices (client_symbol);

            CREATE TABLE IF NOT EXISTS cash_operations (
                id INTEGER NOT NULL,
                source TEXT NOT NULL,
                date TEXT NOT NULL,
                amount REAL NOT NULL,
                money_in INTEGER NOT NULL,
                title TEXT NOT NULL,
                counterparty_name TEXT NOT NULL,
                category TEXT NOT NULL,
                PRIMARY KEY (source, id)
            );

            CREATE INDEX IF NOT EXISTS ix_cash_operations_date ON cash_operations (date);

            CREATE TABLE IF NOT EXISTS sync_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        using var setVersionCmd = connection.CreateCommand();
        setVersionCmd.CommandText = $"PRAGMA user_version = {SchemaVersion};";
        setVersionCmd.ExecuteNonQuery();
    }

    public void UpsertInvoices(IEnumerable<Invoice> invoices)
    {
        using var pooled = Open();
        var connection = pooled.Connection;
        using var transaction = connection.BeginTransaction();
        UpsertInvoicesCore(connection, transaction, invoices);
        transaction.Commit();
    }

    private static void UpsertInvoicesCore(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<Invoice> invoices)
    {
        using var deleteLines = connection.CreateCommand();
        deleteLines.Transaction = transaction;
        deleteLines.CommandText = "DELETE FROM invoice_lines WHERE invoice_id = $id";
        var deleteLinesId = deleteLines.CreateParameter();
        deleteLinesId.ParameterName = "$id";
        deleteLines.Parameters.Add(deleteLinesId);

        using var upsertInvoice = connection.CreateCommand();
        upsertInvoice.Transaction = transaction;
        upsertInvoice.CommandText = """
            INSERT INTO invoices
                (id, full_number, issue_date, due_date, kind, direction, client_symbol, client_name,
                 client_tax_id, currency, net_total, gross_total, vat_total, correction_of_id, notes)
            VALUES
                ($id, $fullNumber, $issueDate, $dueDate, $kind, $direction, $clientSymbol, $clientName,
                 $clientTaxId, $currency, $netTotal, $grossTotal, $vatTotal, $correctionOfId, $notes)
            ON CONFLICT(id) DO UPDATE SET
                full_number = excluded.full_number,
                issue_date = excluded.issue_date,
                due_date = excluded.due_date,
                kind = excluded.kind,
                direction = excluded.direction,
                client_symbol = excluded.client_symbol,
                client_name = excluded.client_name,
                client_tax_id = excluded.client_tax_id,
                currency = excluded.currency,
                net_total = excluded.net_total,
                gross_total = excluded.gross_total,
                vat_total = excluded.vat_total,
                correction_of_id = excluded.correction_of_id,
                notes = excluded.notes;
            """;

        using var insertLine = connection.CreateCommand();
        insertLine.Transaction = transaction;
        insertLine.CommandText = """
            INSERT INTO invoice_lines
                (invoice_id, line_no, product_symbol, description, quantity, unit,
                 unit_net_price, vat_rate, net_value, gross_value)
            VALUES
                ($invoiceId, $lineNo, $productSymbol, $description, $quantity, $unit,
                 $unitNetPrice, $vatRate, $netValue, $grossValue);
            """;

        foreach (var invoice in invoices)
        {
            deleteLinesId.Value = invoice.Id;
            deleteLines.ExecuteNonQuery();

            upsertInvoice.Parameters.Clear();
            upsertInvoice.Parameters.AddWithValue("$id", invoice.Id);
            upsertInvoice.Parameters.AddWithValue("$fullNumber", invoice.FullNumber);
            upsertInvoice.Parameters.AddWithValue("$issueDate", invoice.IssueDate.ToString("yyyy-MM-dd"));
            upsertInvoice.Parameters.AddWithValue("$dueDate", invoice.DueDate?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
            upsertInvoice.Parameters.AddWithValue("$kind", invoice.Kind.ToString());
            upsertInvoice.Parameters.AddWithValue("$direction", invoice.Direction.ToString());
            upsertInvoice.Parameters.AddWithValue("$clientSymbol", invoice.ClientSymbol);
            upsertInvoice.Parameters.AddWithValue("$clientName", invoice.ClientName);
            upsertInvoice.Parameters.AddWithValue("$clientTaxId", invoice.ClientTaxId ?? (object)DBNull.Value);
            upsertInvoice.Parameters.AddWithValue("$currency", invoice.Currency);
            upsertInvoice.Parameters.AddWithValue("$netTotal", invoice.NetTotal);
            upsertInvoice.Parameters.AddWithValue("$grossTotal", invoice.GrossTotal);
            upsertInvoice.Parameters.AddWithValue("$vatTotal", invoice.VatTotal);
            upsertInvoice.Parameters.AddWithValue("$correctionOfId", invoice.CorrectionOfId ?? (object)DBNull.Value);
            upsertInvoice.Parameters.AddWithValue("$notes", invoice.Notes ?? (object)DBNull.Value);
            upsertInvoice.ExecuteNonQuery();

            foreach (var line in invoice.Lines)
            {
                insertLine.Parameters.Clear();
                insertLine.Parameters.AddWithValue("$invoiceId", invoice.Id);
                insertLine.Parameters.AddWithValue("$lineNo", line.LineNo);
                insertLine.Parameters.AddWithValue("$productSymbol", line.ProductSymbol);
                insertLine.Parameters.AddWithValue("$description", line.Description);
                insertLine.Parameters.AddWithValue("$quantity", line.Quantity);
                insertLine.Parameters.AddWithValue("$unit", line.Unit);
                insertLine.Parameters.AddWithValue("$unitNetPrice", line.UnitNetPrice);
                insertLine.Parameters.AddWithValue("$vatRate", line.VatRate);
                insertLine.Parameters.AddWithValue("$netValue", line.NetValue);
                insertLine.Parameters.AddWithValue("$grossValue", line.GrossValue);
                insertLine.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Deletes every cached invoice issued within [<paramref name="from"/>, <paramref name="to"/>]
    /// (lines cascade). Call this right before re-upserting a freshly-read window from nexo, so a
    /// document deleted or voided in nexo since the last sync actually disappears from the cache
    /// too - upsert alone only ever adds or updates rows, it never removes one that nexo no
    /// longer returns. Prefer <see cref="ReplaceWindow"/> when also re-upserting the same window
    /// afterward - it does both in one transaction, so a crash in between cannot leave the window
    /// empty.
    /// </summary>
    public void DeleteInvoicesIssuedBetween(DateOnly from, DateOnly to)
    {
        using var pooled = Open();
        var connection = pooled.Connection;
        using var transaction = connection.BeginTransaction();
        DeleteInvoicesIssuedBetweenCore(connection, transaction, from, to);
        transaction.Commit();
    }

    private static void DeleteInvoicesIssuedBetweenCore(SqliteConnection connection, SqliteTransaction transaction, DateOnly from, DateOnly to)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM invoices WHERE issue_date >= $from AND issue_date <= $to;";
        cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Same idea as <see cref="DeleteInvoicesIssuedBetween"/>, for bank/cash operations.</summary>
    public void DeleteCashOperationsBetween(DateOnly from, DateOnly to)
    {
        using var pooled = Open();
        var connection = pooled.Connection;
        using var transaction = connection.BeginTransaction();
        DeleteCashOperationsBetweenCore(connection, transaction, from, to);
        transaction.Commit();
    }

    private static void DeleteCashOperationsBetweenCore(SqliteConnection connection, SqliteTransaction transaction, DateOnly from, DateOnly to)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM cash_operations WHERE date >= $from AND date <= $to;";
        cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Replaces the cached window [<paramref name="from"/>, <paramref name="to"/>] with freshly
    /// read data, all in a single transaction: deletes any invoices/cash operations currently
    /// cached in that window, then upserts the new ones. A sync that instead deleted and
    /// upserted in separate auto-committed calls (the original design) could crash or throw
    /// between them - e.g. on a constraint violation partway through a 3-year full resync - and
    /// leave the whole window empty until the next sync happens to succeed, with every tool call
    /// in between silently seeing zero invoices for that period. Doing it as one transaction means
    /// a failure rolls back to the window's previous (still-correct) contents instead.
    /// </summary>
    public void ReplaceWindow(
        DateOnly from, DateOnly to,
        IEnumerable<Invoice> salesInvoices, IEnumerable<Invoice> purchaseInvoices, IEnumerable<CashOperation> cashOperations)
    {
        using var pooled = Open();
        var connection = pooled.Connection;
        using var transaction = connection.BeginTransaction();

        DeleteInvoicesIssuedBetweenCore(connection, transaction, from, to);
        DeleteCashOperationsBetweenCore(connection, transaction, from, to);
        UpsertInvoicesCore(connection, transaction, salesInvoices);
        UpsertInvoicesCore(connection, transaction, purchaseInvoices);
        UpsertCashOperationsCore(connection, transaction, cashOperations);

        transaction.Commit();
    }

    public List<Invoice> GetInvoices(
        DateOnly? from = null, DateOnly? to = null, string? clientSymbol = null,
        DocumentKind? kind = null, Direction? direction = null)
    {
        using var pooled = Open();
        var connection = pooled.Connection;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, full_number, issue_date, due_date, kind, direction, client_symbol, client_name,
                   client_tax_id, currency, net_total, gross_total, vat_total, correction_of_id, notes
            FROM invoices
            WHERE ($from IS NULL OR issue_date >= $from)
              AND ($to IS NULL OR issue_date <= $to)
              AND ($clientSymbol IS NULL OR client_symbol = $clientSymbol)
              AND ($kind IS NULL OR kind = $kind)
              AND ($direction IS NULL OR direction = $direction)
            ORDER BY issue_date;
            """;
        cmd.Parameters.AddWithValue("$from", from?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$to", to?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$clientSymbol", clientSymbol ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", kind?.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$direction", direction?.ToString() ?? (object)DBNull.Value);

        var invoices = new List<(int Id, string FullNumber, DateOnly IssueDate, DateOnly? DueDate, DocumentKind Kind,
            Direction Direction, string ClientSymbol, string ClientName, string? ClientTaxId, string Currency,
            decimal NetTotal, decimal GrossTotal, decimal VatTotal, int? CorrectionOfId, string? Notes)>();

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                invoices.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    DateOnly.Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : DateOnly.Parse(reader.GetString(3)),
                    Enum.Parse<DocumentKind>(reader.GetString(4)),
                    Enum.Parse<Direction>(reader.GetString(5)),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetString(9),
                    Convert.ToDecimal(reader.GetDouble(10)),
                    Convert.ToDecimal(reader.GetDouble(11)),
                    Convert.ToDecimal(reader.GetDouble(12)),
                    reader.IsDBNull(13) ? null : reader.GetInt32(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14)));
            }
        }

        var result = new List<Invoice>(invoices.Count);
        foreach (var inv in invoices)
        {
            result.Add(new Invoice(
                inv.Id, inv.FullNumber, inv.IssueDate, inv.DueDate, inv.Kind, inv.Direction,
                inv.ClientSymbol, inv.ClientName, inv.ClientTaxId, inv.Currency, inv.NetTotal, inv.GrossTotal,
                inv.VatTotal, inv.CorrectionOfId, inv.Notes, GetLines(connection, inv.Id)));
        }
        return result;
    }

    private static List<InvoiceLine> GetLines(SqliteConnection connection, int invoiceId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT line_no, product_symbol, description, quantity, unit, unit_net_price,
                   vat_rate, net_value, gross_value
            FROM invoice_lines
            WHERE invoice_id = $invoiceId
            ORDER BY line_no;
            """;
        cmd.Parameters.AddWithValue("$invoiceId", invoiceId);

        var lines = new List<InvoiceLine>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(new InvoiceLine(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                Convert.ToDecimal(reader.GetDouble(3)),
                reader.GetString(4),
                Convert.ToDecimal(reader.GetDouble(5)),
                Convert.ToDecimal(reader.GetDouble(6)),
                Convert.ToDecimal(reader.GetDouble(7)),
                Convert.ToDecimal(reader.GetDouble(8))));
        }
        return lines;
    }

    public void UpsertCashOperations(IEnumerable<CashOperation> operations)
    {
        using var pooled = Open();
        var connection = pooled.Connection;
        using var transaction = connection.BeginTransaction();
        UpsertCashOperationsCore(connection, transaction, operations);
        transaction.Commit();
    }

    private static void UpsertCashOperationsCore(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<CashOperation> operations)
    {
        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO cash_operations
                (id, source, date, amount, money_in, title, counterparty_name, category)
            VALUES
                ($id, $source, $date, $amount, $moneyIn, $title, $counterpartyName, $category)
            ON CONFLICT(source, id) DO UPDATE SET
                date = excluded.date,
                amount = excluded.amount,
                money_in = excluded.money_in,
                title = excluded.title,
                counterparty_name = excluded.counterparty_name,
                category = excluded.category;
            """;

        foreach (var op in operations)
        {
            upsert.Parameters.Clear();
            upsert.Parameters.AddWithValue("$id", op.Id);
            upsert.Parameters.AddWithValue("$date", op.Date.ToString("yyyy-MM-dd"));
            upsert.Parameters.AddWithValue("$amount", op.Amount);
            upsert.Parameters.AddWithValue("$moneyIn", op.MoneyIn ? 1 : 0);
            upsert.Parameters.AddWithValue("$title", op.Title);
            upsert.Parameters.AddWithValue("$counterpartyName", op.CounterpartyName);
            upsert.Parameters.AddWithValue("$category", op.Category.ToString());
            upsert.Parameters.AddWithValue("$source", op.Source);
            upsert.ExecuteNonQuery();
        }
    }

    public List<CashOperation> GetCashOperations(DateOnly? from = null, DateOnly? to = null, bool? moneyIn = null)
    {
        using var pooled = Open();
        var connection = pooled.Connection;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, date, amount, money_in, title, counterparty_name, category, source
            FROM cash_operations
            WHERE ($from IS NULL OR date >= $from)
              AND ($to IS NULL OR date <= $to)
              AND ($moneyIn IS NULL OR money_in = $moneyIn)
            ORDER BY date;
            """;
        cmd.Parameters.AddWithValue("$from", from?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$to", to?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$moneyIn", moneyIn is null ? (object)DBNull.Value : (moneyIn.Value ? 1 : 0));

        var result = new List<CashOperation>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CashOperation(
                reader.GetInt32(0),
                DateOnly.Parse(reader.GetString(1)),
                Convert.ToDecimal(reader.GetDouble(2)),
                reader.GetInt32(3) != 0,
                reader.GetString(4),
                reader.GetString(5),
                Enum.Parse<CashFlowCategory>(reader.GetString(6)),
                reader.GetString(7)));
        }
        return result;
    }

    public DateTime? GetLastSyncUtc() => GetSyncState("last_sync_utc");

    public void SetLastSyncUtc(DateTime utc) => SetSyncState("last_sync_utc", utc);

    /// <summary>
    /// Separate from <see cref="GetLastSyncUtc"/>/<see cref="SetLastSyncUtc"/>: tracks the last
    /// time a *full* re-sync (the whole retained window, not just the narrow incremental one) ran
    /// - see SyncBackgroundService for why both exist. A narrow incremental sync alone would never
    /// pick up a document nexo users backdate by more than its own overlap window, nor notice one
    /// deleted outside that window; a periodic full resync (delete-then-reinsert over the whole
    /// window) catches both.
    /// </summary>
    public DateTime? GetLastFullSyncUtc() => GetSyncState("last_full_sync_utc");

    public void SetLastFullSyncUtc(DateTime utc) => SetSyncState("last_full_sync_utc", utc);

    private DateTime? GetSyncState(string key)
    {
        using var pooled = Open();
        var connection = pooled.Connection;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM sync_state WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var value = cmd.ExecuteScalar() as string;
        return value is null ? null : DateTime.Parse(value).ToUniversalTime();
    }

    private void SetSyncState(string key, DateTime utc)
    {
        using var pooled = Open();
        var connection = pooled.Connection;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_state (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", utc.ToUniversalTime().ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _keepAlive?.Dispose();
}
