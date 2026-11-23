using NexoMcp.Core.Analytics;
using NexoMcp.Core.Models;
using Xunit;
using static NexoMcp.Core.Tests.TestInvoices;

namespace NexoMcp.Core.Tests;

public class RevenueAnalyzerTests
{
    [Fact]
    public void Groups_invoices_into_calendar_months_and_sums_net()
    {
        var invoices = new List<Invoice>
        {
            Invoice(1, new DateOnly(2025, 1, 10), "ACME", 100m),
            Invoice(2, new DateOnly(2025, 1, 20), "ACME", 50m),
            Invoice(3, new DateOnly(2025, 2, 5), "ACME", 200m),
        };

        // asOf = March: February just finished, so it is the series' last entry.
        var months = RevenueAnalyzer.MonthlyBreakdown(invoices, asOf: new DateOnly(2025, 3, 1));

        Assert.Equal(2, months.Count);
        Assert.Equal(150m, months[0].Net);
        Assert.Equal(2, months[0].Count);
        Assert.Equal(200m, months[1].Net);
    }

    [Fact]
    public void Fills_months_with_no_invoices_as_zero_instead_of_skipping_them()
    {
        // The real bug this guards against: a trailing-12-month average that only looks at
        // months with data would treat January-this-year and January-last-year as consecutive.
        var invoices = new List<Invoice>
        {
            Invoice(1, new DateOnly(2025, 1, 10), "ACME", 1200m),
            // February and March: no invoices at all.
            Invoice(2, new DateOnly(2025, 4, 5), "ACME", 300m),
        };

        var months = RevenueAnalyzer.MonthlyBreakdown(invoices, asOf: new DateOnly(2025, 5, 1));

        Assert.Equal(4, months.Count); // Jan, Feb, Mar, Apr - not just the 2 months with data.
        Assert.Equal((2025, 1), (months[0].Year, months[0].Month));
        Assert.Equal(1200m, months[0].Net);
        Assert.Equal((2025, 2), (months[1].Year, months[1].Month));
        Assert.Equal(0m, months[1].Net);
        Assert.Equal(0, months[1].Count);
        Assert.Equal((2025, 3), (months[2].Year, months[2].Month));
        Assert.Equal(0m, months[2].Net);
        Assert.Equal((2025, 4), (months[3].Year, months[3].Month));
        Assert.Equal(300m, months[3].Net);
    }

    [Fact]
    public void Excludes_the_current_still_in_progress_month()
    {
        var invoices = new List<Invoice>
        {
            Invoice(1, new DateOnly(2025, 5, 10), "ACME", 100m),
            Invoice(2, new DateOnly(2025, 6, 1), "ACME", 999m),
        };

        // "today" falls in June - June is not yet a complete month, so it must not appear.
        var months = RevenueAnalyzer.MonthlyBreakdown(invoices, asOf: new DateOnly(2025, 6, 15));

        var month = Assert.Single(months);
        Assert.Equal((2025, 5), (month.Year, month.Month));
    }

    [Fact]
    public void Returns_empty_when_all_data_is_in_the_current_incomplete_month()
    {
        var today = new DateOnly(2025, 6, 15);
        var invoices = new List<Invoice> { Invoice(1, today, "ACME", 100m) };

        var months = RevenueAnalyzer.MonthlyBreakdown(invoices, today);

        Assert.Empty(months);
    }

    [Fact]
    public void Excludes_proforma_invoices_from_revenue()
    {
        var invoices = new List<Invoice>
        {
            Invoice(1, new DateOnly(2025, 1, 10), "ACME", 100m, DocumentKind.Proforma),
            Invoice(2, new DateOnly(2025, 1, 20), "ACME", 50m),
        };

        var months = RevenueAnalyzer.MonthlyBreakdown(invoices, asOf: new DateOnly(2025, 2, 1));

        var month = Assert.Single(months);
        Assert.Equal(50m, month.Net);
    }

    [Fact]
    public void Nets_out_corrections_against_the_original_invoice()
    {
        var invoices = new List<Invoice>
        {
            Invoice(1, new DateOnly(2025, 1, 10), "ACME", 100m),
            Invoice(2, new DateOnly(2025, 1, 15), "ACME", -30m, DocumentKind.Correction),
        };

        var months = RevenueAnalyzer.MonthlyBreakdown(invoices, asOf: new DateOnly(2025, 2, 1));

        var month = Assert.Single(months);
        Assert.Equal(70m, month.Net);
    }

    [Fact]
    public void Average_monthly_net_respects_trailing_window()
    {
        var months = new List<MonthlyAmount>
        {
            new(2024, 12, 1000m, 1230m, 1),
            new(2025, 1, 100m, 123m, 1),
            new(2025, 2, 200m, 246m, 1),
        };

        var trailingTwo = RevenueAnalyzer.AverageMonthlyNet(months, lastNMonths: 2);

        Assert.Equal(150m, trailingTwo);
    }

    [Fact]
    public void Average_monthly_net_over_a_gap_year_is_not_misled_by_sparse_data()
    {
        // The exact scenario a live run caught: only two Januaries, a year apart, with nothing
        // in between. A naive "average over rows with data" would see them as back-to-back.
        var invoices = new List<Invoice>
        {
            Invoice(1, new DateOnly(2024, 1, 15), "ACME", 1200m),
            Invoice(2, new DateOnly(2025, 1, 15), "ACME", 1200m),
        };

        var months = RevenueAnalyzer.MonthlyBreakdown(invoices, asOf: new DateOnly(2025, 2, 1));
        var averageOverTrailing12 = RevenueAnalyzer.AverageMonthlyNet(months, lastNMonths: 12);

        Assert.Equal(13, months.Count); // Jan 2024 through Jan 2025 inclusive, gaps filled with 0.
        Assert.Equal(100m, averageOverTrailing12); // 1200 spread over 12 real calendar months, not 2.
    }
}
