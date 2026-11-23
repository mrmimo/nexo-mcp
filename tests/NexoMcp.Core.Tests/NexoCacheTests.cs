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
}
