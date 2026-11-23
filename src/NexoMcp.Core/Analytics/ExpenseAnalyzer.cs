using NexoMcp.Core.Models;

namespace NexoMcp.Core.Analytics;

public sealed record MonthlyExpenseBreakdown(int Year, int Month, decimal Purchases, decimal Zus, decimal Tax, decimal OtherCash, decimal Total);

/// <summary>
/// Costs come from two different sources in nexo, combined here into a dense calendar-month
/// series (see <see cref="RevenueAnalyzer"/> for why density matters - the same gap/current-month
/// bug applies equally to costs): purchase invoices (supplier documents, same shape as sales
/// invoices but <see cref="Direction.Purchase"/>) and bank/cash outflows that never have an
/// invoice at all - which is how ZUS and tax office payments actually show up. See
/// SferaCashFlowReader for how <see cref="CashFlowCategory"/> is derived, and for why operations
/// that settle a document, or move money between the company's own accounts, must never reach
/// this analyzer at all (counting them here would double the real cost).
/// </summary>
public static class ExpenseAnalyzer
{
    public static IReadOnlyList<MonthlyExpenseBreakdown> MonthlyBreakdown(
        IEnumerable<Invoice> purchaseInvoices, IEnumerable<CashOperation> cashOperations, DateOnly? asOf = null)
    {
        var purchasesByMonth = purchaseInvoices
            .Where(i => i.Direction == Direction.Purchase && i.Kind != DocumentKind.Proforma)
            .GroupBy(i => (i.IssueDate.Year, i.IssueDate.Month))
            .ToDictionary(g => g.Key, g => g.Sum(i => i.NetTotal));

        var outflows = cashOperations.Where(o => !o.MoneyIn).ToList();
        var zusByMonth = SumByMonth(outflows.Where(o => o.Category == CashFlowCategory.Zus));
        var taxByMonth = SumByMonth(outflows.Where(o => o.Category == CashFlowCategory.Tax));
        var otherByMonth = SumByMonth(outflows.Where(o => o.Category == CashFlowCategory.Other));

        var allKeys = purchasesByMonth.Keys
            .Concat(zusByMonth.Keys)
            .Concat(taxByMonth.Keys)
            .Concat(otherByMonth.Keys)
            .ToList();
        if (allKeys.Count == 0)
            return [];

        var earliest = allKeys.Select(k => new DateOnly(k.Year, k.Month, 1)).Min();
        var lastComplete = RevenueAnalyzer.LastCompleteMonth(asOf);
        if (lastComplete < earliest)
            return [];

        var result = new List<MonthlyExpenseBreakdown>();
        for (var cursor = earliest; cursor <= lastComplete; cursor = cursor.AddMonths(1))
        {
            var key = (cursor.Year, cursor.Month);
            decimal purchases = purchasesByMonth.GetValueOrDefault(key);
            decimal zus = zusByMonth.GetValueOrDefault(key);
            decimal tax = taxByMonth.GetValueOrDefault(key);
            decimal other = otherByMonth.GetValueOrDefault(key);
            result.Add(new MonthlyExpenseBreakdown(cursor.Year, cursor.Month, purchases, zus, tax, other, purchases + zus + tax + other));
        }
        return result;
    }

    public static IReadOnlyList<MonthlyAmount> ToMonthlyAmounts(IReadOnlyList<MonthlyExpenseBreakdown> breakdown) =>
        breakdown.Select(b => new MonthlyAmount(b.Year, b.Month, b.Total, b.Total, 0)).ToList();

    private static Dictionary<(int Year, int Month), decimal> SumByMonth(IEnumerable<CashOperation> operations) =>
        operations
            .GroupBy(o => (o.Date.Year, o.Date.Month))
            .ToDictionary(g => g.Key, g => g.Sum(o => o.Amount));
}
