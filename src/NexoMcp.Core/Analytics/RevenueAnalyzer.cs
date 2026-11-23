using NexoMcp.Core.Models;

namespace NexoMcp.Core.Analytics;

public sealed record MonthlyRevenue(int Year, int Month, decimal Net, decimal Gross, int InvoiceCount);

/// <summary>
/// Aggregates invoices into calendar-month revenue. Proforma invoices are excluded (they are not
/// yet a sale); corrections are included as-is on the assumption that nexo already stores their
/// net/gross totals as signed deltas against the original invoice, so summing invoices and
/// corrections together nets out correctly.
/// </summary>
public static class RevenueAnalyzer
{
    public static IReadOnlyList<MonthlyRevenue> MonthlyBreakdown(IEnumerable<Invoice> invoices)
    {
        return invoices
            .Where(i => i.Kind != DocumentKind.Proforma)
            .GroupBy(i => (i.IssueDate.Year, i.IssueDate.Month))
            .Select(g => new MonthlyRevenue(
                g.Key.Year, g.Key.Month,
                g.Sum(i => i.NetTotal),
                g.Sum(i => i.GrossTotal),
                g.Count()))
            .OrderBy(m => m.Year).ThenBy(m => m.Month)
            .ToList();
    }

    public static decimal AverageMonthlyNet(IReadOnlyList<MonthlyRevenue> months, int? lastNMonths = null)
    {
        var slice = Slice(months, lastNMonths);
        return slice.Count == 0 ? 0m : slice.Average(m => m.Net);
    }

    public static decimal AverageMonthlyGross(IReadOnlyList<MonthlyRevenue> months, int? lastNMonths = null)
    {
        var slice = Slice(months, lastNMonths);
        return slice.Count == 0 ? 0m : slice.Average(m => m.Gross);
    }

    private static IReadOnlyList<MonthlyRevenue> Slice(IReadOnlyList<MonthlyRevenue> months, int? lastNMonths) =>
        lastNMonths is null ? months : months.TakeLast(lastNMonths.Value).ToList();
}
