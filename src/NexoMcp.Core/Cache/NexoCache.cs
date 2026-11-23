using Microsoft.Data.Sqlite;
using NexoMcp.Core.Models;

namespace NexoMcp.Core.Cache;

/// <summary>
/// Local SQLite mirror of invoices (sales and purchase) and cash/bank operations read from nexo.
/// Analytics run against this cache instead of re-querying the live nexo database (and the Sfera
/// API) on every request.
/// </summary>
public sealed class NexoCache : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>
    /// <paramref name="connectionString"/> is a full Microsoft.Data.Sqlite connection string,
    /// e.g. "Data Source=cache/invoices.db" for a file or "Data Source=:memory:" for a
    /// throwaway in-memory database (tests). The connection is kept open for the cache's
    /// lifetime, which is required for ":memory:" to survive between calls.
    /// </summary>
    public NexoCache(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
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
    }

    public void UpsertInvoices(IEnumerable<Invoice> invoices)
    {
        using var transaction = _connection.BeginTransaction();

        using var deleteLines = _connection.CreateCommand();
        deleteLines.Transaction = transaction;
        deleteLines.CommandText = "DELETE FROM invoice_lines WHERE invoice_id = $id";
        var deleteLinesId = deleteLines.CreateParameter();
        deleteLinesId.ParameterName = "$id";
        deleteLines.Parameters.Add(deleteLinesId);

        using var upsertInvoice = _connection.CreateCommand();
        upsertInvoice.Transaction = transaction;
        upsertInvoice.CommandText = """
            INSERT INTO invoices
                (id, full_number, issue_date, due_date, kind, direction, client_symbol, client_name,
                 currency, net_total, gross_total, vat_total, correction_of_id, notes)
            VALUES
                ($id, $fullNumber, $issueDate, $dueDate, $kind, $direction, $clientSymbol, $clientName,
                 $currency, $netTotal, $grossTotal, $vatTotal, $correctionOfId, $notes)
            ON CONFLICT(id) DO UPDATE SET
                full_number = excluded.full_number,
                issue_date = excluded.issue_date,
                due_date = excluded.due_date,
                kind = excluded.kind,
                direction = excluded.direction,
                client_symbol = excluded.client_symbol,
                client_name = excluded.client_name,
                currency = excluded.currency,
                net_total = excluded.net_total,
                gross_total = excluded.gross_total,
                vat_total = excluded.vat_total,
                correction_of_id = excluded.correction_of_id,
                notes = excluded.notes;
            """;

        using var insertLine = _connection.CreateCommand();
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

        transaction.Commit();
    }

    public List<Invoice> GetInvoices(
        DateOnly? from = null, DateOnly? to = null, string? clientSymbol = null,
        DocumentKind? kind = null, Direction? direction = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, full_number, issue_date, due_date, kind, direction, client_symbol, client_name,
                   currency, net_total, gross_total, vat_total, correction_of_id, notes
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
            Direction Direction, string ClientSymbol, string ClientName, string Currency, decimal NetTotal,
            decimal GrossTotal, decimal VatTotal, int? CorrectionOfId, string? Notes)>();

        using var reader = cmd.ExecuteReader();
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
                reader.GetString(8),
                Convert.ToDecimal(reader.GetDouble(9)),
                Convert.ToDecimal(reader.GetDouble(10)),
                Convert.ToDecimal(reader.GetDouble(11)),
                reader.IsDBNull(12) ? null : reader.GetInt32(12),
                reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        reader.Close();

        var result = new List<Invoice>(invoices.Count);
        foreach (var inv in invoices)
        {
            result.Add(new Invoice(
                inv.Id, inv.FullNumber, inv.IssueDate, inv.DueDate, inv.Kind, inv.Direction,
                inv.ClientSymbol, inv.ClientName, inv.Currency, inv.NetTotal, inv.GrossTotal,
                inv.VatTotal, inv.CorrectionOfId, inv.Notes, GetLines(inv.Id)));
        }
        return result;
    }

    private List<InvoiceLine> GetLines(int invoiceId)
    {
        using var cmd = _connection.CreateCommand();
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
        using var transaction = _connection.BeginTransaction();
        using var upsert = _connection.CreateCommand();
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

        transaction.Commit();
    }

    public List<CashOperation> GetCashOperations(DateOnly? from = null, DateOnly? to = null, bool? moneyIn = null)
    {
        using var cmd = _connection.CreateCommand();
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

    public DateTime? GetLastSyncUtc()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM sync_state WHERE key = 'last_sync_utc';";
        var value = cmd.ExecuteScalar() as string;
        return value is null ? null : DateTime.Parse(value).ToUniversalTime();
    }

    public void SetLastSyncUtc(DateTime utc)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_state (key, value) VALUES ('last_sync_utc', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$value", utc.ToUniversalTime().ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
