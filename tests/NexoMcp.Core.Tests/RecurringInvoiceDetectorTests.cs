using NexoMcp.Core.Analytics;
using NexoMcp.Core.Models;
using Xunit;
using static NexoMcp.Core.Tests.TestInvoices;

namespace NexoMcp.Core.Tests;

public class RecurringInvoiceDetectorTests
{
    [Fact]
    public void Detects_monthly_series_for_regular_client_product_pair()
    {
        var invoices = new List<Invoice>();
        var start = new DateOnly(2025, 1, 5);
        for (int i = 0; i < 6; i++)
        {
            var date = start.AddMonths(i);
            invoices.Add(Invoice(i + 1, date, "ACME", 100m,
                lines: [Line(1, "HOSTING", "Usluga hostingu", 100m)]));
        }

        var series = RecurringInvoiceDetector.Detect(invoices);

        var s = Assert.Single(series);
        Assert.Equal("ACME", s.ClientSymbol);
        Assert.Equal("HOSTING", s.ProductSymbol);
        Assert.Equal(Periodicity.Monthly, s.Periodicity);
        Assert.Equal(6, s.Occurrences.Count);
    }

    [Fact]
    public void Labels_sporadic_purchases_as_irregular_instead_of_hiding_them()
    {
        // Hitting minOccurrences at all is still worth surfacing to a human looking for
        // recurring clients - honestly labeled "Irregular", not silently dropped (a client
        // billed almost-but-not-quite regularly is exactly this tool's reason to exist).
        var invoices = new List<Invoice>
        {
            Invoice(1, new DateOnly(2025, 1, 5), "ACME", 100m, lines: [Line(1, "CABLE", "Kabel USB", 100m)]),
            Invoice(2, new DateOnly(2025, 3, 20), "ACME", 50m, lines: [Line(1, "CABLE", "Kabel USB", 50m)]),
            Invoice(3, new DateOnly(2025, 9, 1), "ACME", 70m, lines: [Line(1, "CABLE", "Kabel USB", 70m)]),
        };

        var series = RecurringInvoiceDetector.Detect(invoices);

        var s = Assert.Single(series);
        Assert.Equal(Periodicity.Irregular, s.Periodicity);
        Assert.Equal(3, s.Occurrences.Count);
    }

    [Fact]
    public void Still_names_the_cadence_despite_one_delayed_invoice()
    {
        // Modeled on a real production client: mostly ~30-day gaps with one ~50-day gap where
        // a month was skipped/delayed - still clearly a monthly client to a human reader.
        var dates = new[]
        {
            new DateOnly(2025, 1, 15), new DateOnly(2025, 2, 24), new DateOnly(2025, 3, 16),
            new DateOnly(2025, 4, 14), new DateOnly(2025, 5, 19), new DateOnly(2025, 6, 15),
            new DateOnly(2025, 8, 6), // ~52-day gap - July skipped
            new DateOnly(2025, 9, 14), new DateOnly(2025, 10, 2), new DateOnly(2025, 10, 20),
            new DateOnly(2025, 11, 15),
        };
        var invoices = dates.Select((d, i) => Invoice(i + 1, d, "ACME", 377m,
            lines: [Line(1, "POZ-1", "Za okres", 377m)])).ToList();

        var series = RecurringInvoiceDetector.Detect(invoices);

        var s = Assert.Single(series);
        Assert.Equal(Periodicity.Monthly, s.Periodicity);
        Assert.Equal(11, s.Occurrences.Count);
    }

    [Fact]
    public void Requires_minimum_occurrence_count()
    {
        var invoices = new List<Invoice>
        {
            Invoice(1, new DateOnly(2025, 1, 5), "ACME", 100m),
            Invoice(2, new DateOnly(2025, 2, 5), "ACME", 100m),
        };

        var series = RecurringInvoiceDetector.Detect(invoices, minOccurrences: 3);

        Assert.Empty(series);
    }

    [Theory]
    [InlineData("Abonament za okres od 01.03.2025 do 31.03.2025", "2025-03-01", "2025-03-31")]
    [InlineData("Usluga hostingu za 03.2025", "2025-03-01", "2025-03-31")]
    [InlineData("Czynsz za marzec 2025", "2025-03-01", "2025-03-31")]
    // Real phrasings observed on this company's own live invoices (end date only, no start date).
    [InlineData("Za okres do 25-10-2026", "2026-10-01", "2026-10-25")]
    [InlineData("Za okres do 14-listopad 2026", "2026-11-01", "2026-11-14")]
    public void Parses_known_period_phrasings(string description, string expectedFrom, string expectedTo)
    {
        var result = RecurringInvoiceDetector.TryParsePeriod(description, new DateOnly(2025, 3, 1));

        Assert.NotNull(result);
        Assert.Equal(DateOnly.Parse(expectedFrom), result!.Value.From);
        Assert.Equal(DateOnly.Parse(expectedTo), result!.Value.To);
    }

    [Fact]
    public void Returns_null_period_for_free_text_without_a_recognizable_pattern()
    {
        var result = RecurringInvoiceDetector.TryParsePeriod("Usluga konsultingowa - prace dodatkowe", new DateOnly(2025, 3, 1));

        Assert.Null(result);
    }
}
