using NexoMcp.Core.Models;

namespace NexoMcp.Core.Analytics;

/// <summary>
/// Aggregates sales invoices into calendar-month revenue. Proforma invoices and purchase
/// documents are excluded (a proforma is not yet a sale; a purchase is a cost, see
/// <see cref="ExpenseAnalyzer"/>); corrections are included as-is on the assumption that nexo
/// already stores their net/gross totals as signed deltas against the original invoice, so
/// summing invoices and corrections together nets out correctly.
/// </summary>
public static class RevenueAnalyzer
{
    public static IReadOnlyList<MonthlyAmount> MonthlyBreakdown(IEnumerable<Invoice> invoices)
    {
        return invoices
            .Where(i => i.Direction == Direction.Sale && i.Kind != DocumentKind.Proforma)
            .GroupBy(i => (i.IssueDate.Year, i.IssueDate.Month))
            .Select(g => new MonthlyAmount(
                g.Key.Year, g.Key.Month,
                g.Sum(i => i.NetTotal),
                g.Sum(i => i.GrossTotal),
                g.Count()))
            .OrderBy(m => m.Year).ThenBy(m => m.Month)
            .ToList();
    }

    public static decimal AverageMonthlyNet(IReadOnlyList<MonthlyAmount> months, int? lastNMonths = null)
    {
        var slice = Slice(months, lastNMonths);
        return slice.Count == 0 ? 0m : slice.Average(m => m.Net);
    }

    public static decimal AverageMonthlyGross(IReadOnlyList<MonthlyAmount> months, int? lastNMonths = null)
    {
        var slice = Slice(months, lastNMonths);
        return slice.Count == 0 ? 0m : slice.Average(m => m.Gross);
    }

    private static IReadOnlyList<MonthlyAmount> Slice(IReadOnlyList<MonthlyAmount> months, int? lastNMonths) =>
        lastNMonths is null ? months : months.TakeLast(lastNMonths.Value).ToList();
}
