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

        var months = RevenueAnalyzer.MonthlyBreakdown(invoices);

        Assert.Equal(2, months.Count);
        Assert.Equal(150m, months[0].Net);
        Assert.Equal(2, months[0].InvoiceCount);
        Assert.Equal(200m, months[1].Net);
    }

    [Fact]
    public void Excludes_proforma_invoices_from_revenue()
    {
        var invoices = new List<Invoice>
        {
            Invoice(1, new DateOnly(2025, 1, 10), "ACME", 100m, DocumentKind.Proforma),
            Invoice(2, new DateOnly(2025, 1, 20), "ACME", 50m),
        };

        var months = RevenueAnalyzer.MonthlyBreakdown(invoices);

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

        var months = RevenueAnalyzer.MonthlyBreakdown(invoices);

        var month = Assert.Single(months);
        Assert.Equal(70m, month.Net);
    }

    [Fact]
    public void Average_monthly_net_respects_trailing_window()
    {
        var months = new List<MonthlyRevenue>
        {
            new(2024, 12, 1000m, 1230m, 1),
            new(2025, 1, 100m, 123m, 1),
            new(2025, 2, 200m, 246m, 1),
        };

        var trailingTwo = RevenueAnalyzer.AverageMonthlyNet(months, lastNMonths: 2);

        Assert.Equal(150m, trailingTwo);
    }
}
