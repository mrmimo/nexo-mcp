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
    public void Does_not_flag_irregular_one_off_purchases_as_recurring()
    {
        var invoices = new List<Invoice>
        {
            Invoice(1, new DateOnly(2025, 1, 5), "ACME", 100m, lines: [Line(1, "CABLE", "Kabel USB", 100m)]),
            Invoice(2, new DateOnly(2025, 3, 20), "ACME", 50m, lines: [Line(1, "CABLE", "Kabel USB", 50m)]),
            Invoice(3, new DateOnly(2025, 9, 1), "ACME", 70m, lines: [Line(1, "CABLE", "Kabel USB", 70m)]),
        };

        var series = RecurringInvoiceDetector.Detect(invoices);

        Assert.Empty(series);
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
