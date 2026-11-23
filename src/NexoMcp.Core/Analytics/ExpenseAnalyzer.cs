using NexoMcp.Core.Models;

namespace NexoMcp.Core.Analytics;

public sealed record MonthlyExpenseBreakdown(int Year, int Month, decimal Purchases, decimal Zus, decimal Tax, decimal OtherCash, decimal Total);

/// <summary>
/// Costs come from two different sources in nexo, combined here: purchase invoices (supplier
/// documents, same shape as sales invoices but <see cref="Direction.Purchase"/>) and bank/cash
/// outflows that never have an invoice at all - which is how ZUS and tax office payments
/// actually show up. See SferaCashFlowReader for how <see cref="CashFlowCategory"/> is derived.
/// </summary>
public static class ExpenseAnalyzer
{
    public static IReadOnlyList<MonthlyExpenseBreakdown> MonthlyBreakdown(
        IEnumerable<Invoice> purchaseInvoices, IEnumerable<CashOperation> cashOperations)
    {
        var purchasesByMonth = purchaseInvoices
            .Where(i => i.Direction == Direction.Purchase && i.Kind != DocumentKind.Proforma)
            .GroupBy(i => (i.IssueDate.Year, i.IssueDate.Month))
            .ToDictionary(g => g.Key, g => g.Sum(i => i.NetTotal));

        var outflows = cashOperations.Where(o => !o.MoneyIn).ToList();
        var zusByMonth = SumByMonth(outflows.Where(o => o.Category == CashFlowCategory.Zus));
        var taxByMonth = SumByMonth(outflows.Where(o => o.Category == CashFlowCategory.Tax));
        var otherByMonth = SumByMonth(outflows.Where(o => o.Category == CashFlowCategory.Other));

        var allMonths = purchasesByMonth.Keys
            .Concat(zusByMonth.Keys)
            .Concat(taxByMonth.Keys)
            .Concat(otherByMonth.Keys)
            .Distinct()
            .OrderBy(k => k.Year).ThenBy(k => k.Month);

        return allMonths.Select(key =>
        {
            decimal purchases = purchasesByMonth.GetValueOrDefault(key);
            decimal zus = zusByMonth.GetValueOrDefault(key);
            decimal tax = taxByMonth.GetValueOrDefault(key);
            decimal other = otherByMonth.GetValueOrDefault(key);
            return new MonthlyExpenseBreakdown(key.Year, key.Month, purchases, zus, tax, other, purchases + zus + tax + other);
        }).ToList();
    }

    public static IReadOnlyList<MonthlyAmount> ToMonthlyAmounts(IReadOnlyList<MonthlyExpenseBreakdown> breakdown) =>
        breakdown.Select(b => new MonthlyAmount(b.Year, b.Month, b.Total, b.Total, 0)).ToList();

    private static Dictionary<(int Year, int Month), decimal> SumByMonth(IEnumerable<CashOperation> operations) =>
        operations
            .GroupBy(o => (o.Date.Year, o.Date.Month))
            .ToDictionary(g => g.Key, g => g.Sum(o => o.Amount));
}
