using NexoMcp.Core.Cache;
using NexoMcp.Core.Models;
using Xunit;
using static NexoMcp.Core.Tests.TestInvoices;

namespace NexoMcp.Core.Tests;

public class NexoCacheTests
{
    [Fact]
    public void Round_trips_invoices_and_their_lines_through_sqlite()
    {
        using var cache = new NexoCache("Data Source=:memory:;Cache=Shared");

        var invoice = Invoice(1, new DateOnly(2025, 3, 1), "ACME", 100m,
            lines:
            [
                Line(1, "HOSTING", "Usluga hostingu za 03.2025", 80m),
                Line(2, "DOMENA", "Domena .pl", 20m),
            ]);

        cache.UpsertInvoices([invoice]);

        var loaded = cache.GetInvoices();
        var single = Assert.Single(loaded);
        Assert.Equal(invoice.FullNumber, single.FullNumber);
        Assert.Equal(2, single.Lines.Count);
        Assert.Equal("HOSTING", single.Lines[0].ProductSymbol);
    }

    [Fact]
    public void Upsert_replaces_lines_for_an_already_cached_invoice()
    {
        using var cache = new NexoCache("Data Source=:memory:;Cache=Shared");

        var first = Invoice(1, new DateOnly(2025, 3, 1), "ACME", 100m,
            lines: [Line(1, "HOSTING", "v1", 100m)]);
        cache.UpsertInvoices([first]);

        var updated = Invoice(1, new DateOnly(2025, 3, 1), "ACME", 100m,
            lines: [Line(1, "HOSTING", "v2", 60m), Line(2, "DOMENA", "v2b", 40m)]);
        cache.UpsertInvoices([updated]);

        var loaded = cache.GetInvoices();
        var single = Assert.Single(loaded);
        Assert.Equal(2, single.Lines.Count);
        Assert.Equal("v2", single.Lines[0].Description);
    }

    [Fact]
    public void ReplaceWindow_drops_everything_in_the_window_and_upserts_the_new_set_atomically()
    {
        using var cache = new NexoCache("Data Source=:memory:;Cache=Shared");

        cache.UpsertInvoices(
        [
            Invoice(1, new DateOnly(2025, 3, 10), "ACME", 100m, lines: [Line(1, "HOSTING", "stary", 100m)]),
            Invoice(2, new DateOnly(2025, 6, 1), "ACME", 50m), // outside the window below - must survive.
        ]);
        cache.UpsertCashOperations([
            new CashOperation(1, new DateOnly(2025, 3, 15), 200m, false, "stary", "Ktos", CashFlowCategory.Other, "Bank"),
        ]);

        var from = new DateOnly(2025, 1, 1);
        var to = new DateOnly(2025, 3, 31);
        var newInvoice = Invoice(3, new DateOnly(2025, 3, 10), "ACME", 77m, lines: [Line(1, "HOSTING", "nowy", 77m)]);
        var newOp = new CashOperation(2, new DateOnly(2025, 3, 15), 99m, false, "nowy", "Ktos", CashFlowCategory.Zus, "Bank");

        cache.ReplaceWindow(from, to, [newInvoice], [], [newOp]);

        var invoices = cache.GetInvoices();
        Assert.Equal(2, invoices.Count); // the old in-window invoice (1) is gone; out-of-window (2) and the new one (3) remain.
        Assert.DoesNotContain(invoices, i => i.Id == 1);
        var replaced = Assert.Single(invoices, i => i.Id == 3);
        Assert.Equal("nowy", Assert.Single(replaced.Lines).Description); // lines cascaded with the deleted invoice, not orphaned.
        Assert.Contains(invoices, i => i.Id == 2);

        var ops = cache.GetCashOperations();
        var op = Assert.Single(ops);
        Assert.Equal(2, op.Id);
        Assert.Equal(CashFlowCategory.Zus, op.Category);
    }

    [Fact]
    public void Filters_by_date_range_and_client()
    {
        using var cache = new NexoCache("Data Source=:memory:;Cache=Shared");
        cache.UpsertInvoices(
        [
            Invoice(1, new DateOnly(2025, 1, 1), "ACME", 100m),
            Invoice(2, new DateOnly(2025, 6, 1), "ACME", 100m),
            Invoice(3, new DateOnly(2025, 6, 1), "OTHER", 100m),
        ]);

        var filtered = cache.GetInvoices(from: new DateOnly(2025, 3, 1), clientSymbol: "ACME");

        var single = Assert.Single(filtered);
        Assert.Equal(2, single.Id);
    }

    [Fact]
    public void Tracks_last_sync_timestamp()
    {
        using var cache = new NexoCache("Data Source=:memory:;Cache=Shared");

        Assert.Null(cache.GetLastSyncUtc());

        var now = DateTime.UtcNow;
        cache.SetLastSyncUtc(now);

        var loaded = cache.GetLastSyncUtc();
        Assert.NotNull(loaded);
        Assert.Equal(now, loaded.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Tracks_last_sync_and_last_full_sync_independently()
    {
        using var cache = new NexoCache("Data Source=:memory:;Cache=Shared");

        Assert.Null(cache.GetLastFullSyncUtc());

        var incrementalTime = DateTime.UtcNow;
        cache.SetLastSyncUtc(incrementalTime);
        Assert.Null(cache.GetLastFullSyncUtc()); // setting one must not set the other.

        var fullTime = incrementalTime.AddMinutes(5);
        cache.SetLastFullSyncUtc(fullTime);

        Assert.Equal(incrementalTime, cache.GetLastSyncUtc()!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(fullTime, cache.GetLastFullSyncUtc()!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Filters_by_direction()
    {
        using var cache = new NexoCache("Data Source=:memory:;Cache=Shared");
        cache.UpsertInvoices(
        [
            Invoice(1, new DateOnly(2025, 1, 1), "ACME", 100m, direction: Direction.Sale),
            Invoice(2, new DateOnly(2025, 1, 2), "SUPPLIER", 50m, direction: Direction.Purchase),
        ]);

        var purchasesOnly = cache.GetInvoices(direction: Direction.Purchase);

        var single = Assert.Single(purchasesOnly);
        Assert.Equal(2, single.Id);
    }

    [Fact]
    public void Round_trips_a_client_tax_id_when_present_and_null_when_absent()
    {
        using var cache = new NexoCache("Data Source=:memory:;Cache=Shared");
        var withNip = Invoice(1, new DateOnly(2025, 1, 1), "ACME", 100m) with { ClientTaxId = "1234567890" };
        var withoutNip = Invoice(2, new DateOnly(2025, 1, 2), "OTHER", 100m);
        cache.UpsertInvoices([withNip, withoutNip]);

        var loaded = cache.GetInvoices();

        Assert.Equal("1234567890", loaded.Single(i => i.Id == 1).ClientTaxId);
        Assert.Null(loaded.Single(i => i.Id == 2).ClientTaxId);
    }

    [Fact]
    public void Upserts_and_filters_cash_operations_by_money_direction()
    {
        using var cache = new NexoCache("Data Source=:memory:;Cache=Shared");
        var inflow = new CashOperation(1, new DateOnly(2025, 1, 1), 500m, true, "wplata", "klient", CashFlowCategory.Other, "Bank");
        var outflow = new CashOperation(2, new DateOnly(2025, 1, 2), 300m, false, "ZUS", "Centrala ZUS", CashFlowCategory.Zus, "Bank");
        cache.UpsertCashOperations([inflow, outflow]);

        var outflowsOnly = cache.GetCashOperations(moneyIn: false);

        var single = Assert.Single(outflowsOnly);
        Assert.Equal(2, single.Id);
        Assert.Equal(CashFlowCategory.Zus, single.Category);
    }

    [Fact]
    public void Cash_operations_from_different_sources_do_not_collide_on_the_same_numeric_id()
    {
        // Bank and Kasa operations get their ids from independent nexo sequences - the cache key
        // is (source, id), not id alone, or a bank operation and a cash-register operation that
        // happen to share a numeric id would overwrite each other.
        using var cache = new NexoCache("Data Source=:memory:;Cache=Shared");
        var bankOp = new CashOperation(1, new DateOnly(2025, 1, 1), 100m, false, "a", "x", CashFlowCategory.Other, "Bank");
        var kasaOp = new CashOperation(1, new DateOnly(2025, 1, 1), 200m, false, "b", "y", CashFlowCategory.Other, "Kasa");
        cache.UpsertCashOperations([bankOp, kasaOp]);

        var all = cache.GetCashOperations();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, o => o.Source == "Bank" && o.Amount == 100m);
        Assert.Contains(all, o => o.Source == "Kasa" && o.Amount == 200m);
    }

    [Fact]
    public void Survives_separate_connections_against_a_real_file()
    {
        // The in-memory tests above all share one kept-alive connection; this is the one test
        // that exercises the real, file-backed, connection-per-call path the production cache
        // actually uses (see NexoCache's class doc for why that changed from a single shared
        // connection). A temp file, not ":memory:", so each NexoCache call genuinely opens and
        // closes its own SqliteConnection against on-disk state.
        string path = Path.Combine(Path.GetTempPath(), $"nexo-mcp-test-{Guid.NewGuid():N}.db");
        try
        {
            using var cache = new NexoCache($"Data Source={path}");
            cache.UpsertInvoices([Invoice(1, new DateOnly(2025, 1, 1), "ACME", 100m)]);

            var loaded = cache.GetInvoices();

            var single = Assert.Single(loaded);
            Assert.Equal(1, single.Id);
        }
        finally
        {
            // Microsoft.Data.Sqlite pools the native connection by default - it outlives
            // SqliteConnection.Dispose() unless the pool is cleared, which would otherwise leave
            // the file locked here.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public void Rebuilds_the_cache_instead_of_crashing_when_the_schema_version_does_not_match()
    {
        // Simulates upgrading nexo-mcp against a cache file built by an older schema version:
        // this must drop and recreate the tables, not throw "no such column" on every call.
        string path = Path.Combine(Path.GetTempPath(), $"nexo-mcp-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var cache = new NexoCache($"Data Source={path}"))
                cache.UpsertInvoices([Invoice(1, new DateOnly(2025, 1, 1), "ACME", 100m)]);

            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 999999;"; // deliberately not the real SchemaVersion.
                cmd.ExecuteNonQuery();
            }

            using var reopened = new NexoCache($"Data Source={path}");
            var loaded = reopened.GetInvoices(); // must not throw, and the old row is gone.

            Assert.Empty(loaded);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public void Rebuilds_a_cache_left_over_from_before_schema_versioning_existed()
    {
        // A cache built by a version of this project before PRAGMA user_version was introduced
        // reads back as version 0 today - the exact same value a brand new, empty database also
        // starts at. Simulates that old file directly (hand-built "invoices" table, missing the
        // client_tax_id column added alongside versioning, and no PRAGMA user_version ever set):
        // opening it must rebuild the table, not silently keep its old shape while stamping
        // PRAGMA user_version to the current value - which would hide the mismatch forever after.
        string path = Path.Combine(Path.GetTempPath(), $"nexo-mcp-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE invoices (
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
                    """;
                cmd.ExecuteNonQuery();
            }

            using var reopened = new NexoCache($"Data Source={path}");
            // Must not throw "no such column: client_tax_id" - the whole point of the fix.
            reopened.UpsertInvoices([Invoice(1, new DateOnly(2025, 1, 1), "ACME", 100m)]);
            var loaded = reopened.GetInvoices();

            Assert.Single(loaded);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }
}
