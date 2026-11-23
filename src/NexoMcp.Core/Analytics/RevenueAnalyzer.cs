using NexoMcp.Core.Models;

namespace NexoMcp.Core.Analytics;

/// <summary>
/// Aggregates sales invoices into a dense calendar-month revenue series. Proforma invoices and
/// purchase documents are excluded (a proforma is not yet a sale; a purchase is a cost, see
/// <see cref="ExpenseAnalyzer"/>); corrections are included as-is on the assumption that nexo
/// already stores their net/gross totals as signed deltas against the original invoice, so
/// summing invoices and corrections together nets out correctly - this specific assumption has
/// NOT been independently verified against a real correction document (everything else in this
/// file has been - see README "Status"); if your revenue numbers look off specifically around a
/// correction, this line is the first thing to check.
///
/// The returned series has one entry per calendar month from the earliest invoice through the
/// last *complete* month (never the current, still-in-progress month), with zero-filled entries
/// for months with no invoices at all. This matters: an average or a budget built from only the
/// months that happened to have data would silently skip real zero-revenue months and could
/// span a gap of years without anyone noticing - exactly the bug a live run against production
/// data surfaced (two Januaries a year apart averaging as if they were consecutive months).
/// </summary>
public static class RevenueAnalyzer
{
    public static IReadOnlyList<MonthlyAmount> MonthlyBreakdown(IEnumerable<Invoice> invoices, DateOnly? asOf = null)
    {
        var sums = invoices
            .Where(i => i.Direction == Direction.Sale && i.Kind != DocumentKind.Proforma)
            .GroupBy(i => (i.IssueDate.Year, i.IssueDate.Month))
            .ToDictionary(g => g.Key, g => (Net: g.Sum(i => i.NetTotal), Gross: g.Sum(i => i.GrossTotal), Count: g.Count()));

        if (sums.Count == 0)
            return [];

        var earliest = sums.Keys.Select(k => new DateOnly(k.Year, k.Month, 1)).Min();
        var lastComplete = LastCompleteMonth(asOf);
        if (lastComplete < earliest)
            return [];

        var result = new List<MonthlyAmount>();
        for (var cursor = earliest; cursor <= lastComplete; cursor = cursor.AddMonths(1))
        {
            var key = (cursor.Year, cursor.Month);
            result.Add(sums.TryGetValue(key, out var v)
                ? new MonthlyAmount(cursor.Year, cursor.Month, v.Net, v.Gross, v.Count)
                : new MonthlyAmount(cursor.Year, cursor.Month, 0m, 0m, 0));
        }
        return result;
    }

    /// <summary>The first day of the last calendar month that has fully elapsed as of <paramref name="asOf"/> (default: today).</summary>
    internal static DateOnly LastCompleteMonth(DateOnly? asOf = null)
    {
        var today = asOf ?? DateOnly.FromDateTime(DateTime.Today);
        return new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
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
