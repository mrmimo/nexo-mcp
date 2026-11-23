using System.ComponentModel;
using ModelContextProtocol.Server;
using NexoMcp.Core.Analytics;
using NexoMcp.Core.Cache;
using NexoMcp.Core.Models;

namespace NexoMcp.Server.Tools;

[McpServerToolType]
public sealed class AnalyticsTools(NexoCache cache)
{
    [McpServerTool(Name = "find_recurring_invoices"), Description(
        "Finds every client+product pair billed at least minOccurrences times, labeled by how regular " +
        "the spacing between issue dates actually is: Monthly, Quarterly, Yearly, or Irregular - nothing " +
        "is hidden just for being imperfectly regular (a client billed monthly but with an occasional " +
        "skipped/delayed month still shows up as Monthly). For each series, also attempts to read the " +
        "billing period (e.g. \"za okres od .. do ..\") out of the line description text - nexo has no " +
        "dedicated period field, so a null period on an occurrence means it could not be parsed, not " +
        "that there was no period.")]
    public object FindRecurringInvoices(
        [Description("Minimum number of occurrences before a client+product pair is reported at all. Default 3.")] int minOccurrences = 3)
    {
        var invoices = cache.GetInvoices(direction: Direction.Sale);
        var series = RecurringInvoiceDetector.Detect(invoices, minOccurrences);

        return series.Select(s => new
        {
            s.ClientSymbol,
            s.ClientName,
            s.ProductSymbol,
            s.ProductDescription,
            Periodicity = s.Periodicity.ToString(),
            AverageIntervalDays = Math.Round(s.AverageIntervalDays, 1),
            s.AverageNetValue,
            Occurrences = s.Occurrences.Select(o => new
            {
                o.InvoiceId,
                o.FullNumber,
                IssueDate = o.IssueDate.ToString("yyyy-MM-dd"),
                o.NetValue,
                PeriodFrom = o.ParsedPeriodFrom?.ToString("yyyy-MM-dd"),
                PeriodTo = o.ParsedPeriodTo?.ToString("yyyy-MM-dd"),
            }),
        });
    }

    [McpServerTool(Name = "get_monthly_revenue"), Description(
        "Breaks down cached invoices into calendar-month net/gross revenue, and reports the average " +
        "monthly net revenue (optionally over just the trailing N months).")]
    public object GetMonthlyRevenue(
        [Description("If set, also compute the average over just the last N months instead of the whole history.")] int? trailingMonths = null)
    {
        var invoices = cache.GetInvoices(direction: Direction.Sale);
        var months = RevenueAnalyzer.MonthlyBreakdown(invoices);

        return new
        {
            Months = months.Select(m => new { m.Year, m.Month, m.Net, m.Gross, m.Count }),
            AverageMonthlyNet = RevenueAnalyzer.AverageMonthlyNet(months, trailingMonths),
            AverageMonthlyGross = RevenueAnalyzer.AverageMonthlyGross(months, trailingMonths),
            TrailingMonthsUsed = trailingMonths,
        };
    }

    [McpServerTool(Name = "get_monthly_expenses"), Description(
        "Breaks down cached costs into calendar-month totals: purchase invoices, ZUS transfers, tax " +
        "office transfers, and other bank/cash outflows (payroll, supplier payments made without an " +
        "invoice, etc.) - see README \"Known limitations\" for how ZUS/tax are recognized.")]
    public object GetMonthlyExpenses(
        [Description("If set, also compute the average over just the last N months instead of the whole history.")] int? trailingMonths = null)
    {
        var purchaseInvoices = cache.GetInvoices(direction: Direction.Purchase);
        var cashOperations = cache.GetCashOperations(moneyIn: false);
        var months = ExpenseAnalyzer.MonthlyBreakdown(purchaseInvoices, cashOperations);
        var asAmounts = ExpenseAnalyzer.ToMonthlyAmounts(months);

        return new
        {
            Months = months.Select(m => new { m.Year, m.Month, m.Purchases, m.Zus, m.Tax, m.OtherCash, m.Total }),
            AverageMonthlyTotal = RevenueAnalyzer.AverageMonthlyNet(asAmounts, trailingMonths),
            TrailingMonthsUsed = trailingMonths,
        };
    }

    [McpServerTool(Name = "project_budget"), Description(
        "Projects a simple next-year monthly budget from cached invoice and cash-flow history: revenue " +
        "(from sales invoices), costs (from purchase invoices plus ZUS/tax/other bank-cash outflows - see " +
        "get_monthly_expenses), and the net of the two. " +
        "\"flat\" repeats the trailing-12-month average across every month of the target year. " +
        "\"seasonal\" averages each calendar month's own history separately (so December's budget comes " +
        "from past Decembers, etc.), falling back to the overall average for a month with no history. " +
        "An optional growth rate is applied to revenue and costs independently. This is a simple " +
        "average-based projection, not a forecast model - treat it as a first draft budget.")]
    public object ProjectBudget(
        [Description("The year to project a budget for, e.g. 2027.")] int year,
        [Description("\"flat\" or \"seasonal\". Default \"seasonal\".")] string method = "seasonal",
        [Description("Percentage growth/decline to apply to revenue, e.g. 5 for +5%. Default 0.")] decimal revenueGrowthRatePercent = 0m,
        [Description("Percentage growth/decline to apply to costs. Default 0.")] decimal costGrowthRatePercent = 0m)
    {
        var projectionMethod = method.Equals("flat", StringComparison.OrdinalIgnoreCase)
            ? ProjectionMethod.FlatTrailingAverage
            : ProjectionMethod.SeasonalMonthlyAverage;

        var revenueMonths = RevenueAnalyzer.MonthlyBreakdown(cache.GetInvoices(direction: Direction.Sale));
        var revenueProjection = BudgetProjector.Project(revenueMonths, year, projectionMethod, revenueGrowthRatePercent);

        var expenseMonths = ExpenseAnalyzer.MonthlyBreakdown(
            cache.GetInvoices(direction: Direction.Purchase), cache.GetCashOperations(moneyIn: false));
        var costProjection = BudgetProjector.Project(
            ExpenseAnalyzer.ToMonthlyAmounts(expenseMonths), year, projectionMethod, costGrowthRatePercent);

        var netByMonth = revenueProjection.Months
            .Join(costProjection.Months, r => r.Month, c => c.Month, (r, c) => new
            {
                Month = r.Month,
                Revenue = r.ProjectedNet,
                Cost = c.ProjectedNet,
                Net = r.ProjectedNet - c.ProjectedNet,
            })
            .ToList();

        return new
        {
            revenueProjection.Year,
            Method = revenueProjection.Method.ToString(),
            RevenueGrowthRatePercentApplied = revenueProjection.GrowthRatePercentApplied,
            CostGrowthRatePercentApplied = costProjection.GrowthRatePercentApplied,
            Months = netByMonth,
            AnnualRevenue = revenueProjection.AnnualTotal,
            AnnualCost = costProjection.AnnualTotal,
            AnnualNet = revenueProjection.AnnualTotal - costProjection.AnnualTotal,
            RevenueHistoryMonthsUsed = revenueMonths.Count,
            CostHistoryMonthsUsed = expenseMonths.Count,
        };
    }
}
