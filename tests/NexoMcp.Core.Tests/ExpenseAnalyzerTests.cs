using NexoMcp.Core.Analytics;
using NexoMcp.Core.Models;
using Xunit;
using static NexoMcp.Core.Tests.TestInvoices;

namespace NexoMcp.Core.Tests;

public class ExpenseAnalyzerTests
{
    private static CashOperation Cash(int id, DateOnly date, decimal amount, bool moneyIn, CashFlowCategory category, string source = "Bank") =>
        new(id, date, amount, moneyIn, "tytul", "kontrahent", category, source);

    [Fact]
    public void Combines_purchase_invoices_and_cash_outflows_by_month()
    {
        var purchases = new List<Invoice>
        {
            Invoice(1, new DateOnly(2025, 3, 10), "SUPPLIER", 1000m, direction: Direction.Purchase),
        };
        var cashOps = new List<CashOperation>
        {
            Cash(1, new DateOnly(2025, 3, 15), 500m, moneyIn: false, category: CashFlowCategory.Zus),
            Cash(2, new DateOnly(2025, 3, 20), 300m, moneyIn: false, category: CashFlowCategory.Tax),
            Cash(3, new DateOnly(2025, 3, 25), 100m, moneyIn: false, category: CashFlowCategory.Other),
        };

        var months = ExpenseAnalyzer.MonthlyBreakdown(purchases, cashOps);

        var month = Assert.Single(months);
        Assert.Equal(1000m, month.Purchases);
        Assert.Equal(500m, month.Zus);
        Assert.Equal(300m, month.Tax);
        Assert.Equal(100m, month.OtherCash);
        Assert.Equal(1900m, month.Total);
    }

    [Fact]
    public void Excludes_money_in_operations_and_sale_invoices()
    {
        var sales = new List<Invoice>
        {
            Invoice(1, new DateOnly(2025, 3, 10), "CLIENT", 1000m, direction: Direction.Sale),
        };
        var cashOps = new List<CashOperation>
        {
            Cash(1, new DateOnly(2025, 3, 15), 500m, moneyIn: true, category: CashFlowCategory.Other),
        };

        var months = ExpenseAnalyzer.MonthlyBreakdown(sales, cashOps);

        Assert.Empty(months);
    }

    [Fact]
    public void ToMonthlyAmounts_maps_total_into_net_and_gross()
    {
        var breakdown = new List<MonthlyExpenseBreakdown> { new(2025, 3, 100m, 50m, 20m, 10m, 180m) };

        var amounts = ExpenseAnalyzer.ToMonthlyAmounts(breakdown);

        var amount = Assert.Single(amounts);
        Assert.Equal(180m, amount.Net);
        Assert.Equal(180m, amount.Gross);
    }
}
