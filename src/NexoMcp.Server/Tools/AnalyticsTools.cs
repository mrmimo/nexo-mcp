using System.ComponentModel;
using ModelContextProtocol.Server;
using NexoMcp.Core.Analytics;
using NexoMcp.Core.Cache;

namespace NexoMcp.Server.Tools;

[McpServerToolType]
public sealed class AnalyticsTools(InvoiceCache cache)
{
    [McpServerTool(Name = "find_recurring_invoices"), Description(
        "Finds client+product pairs that are billed on a regular cadence (monthly/quarterly/yearly), " +
        "based on the spacing between their issue dates. For each series, also attempts to read the " +
        "billing period (e.g. \"za okres od .. do ..\") out of the line description text - nexo has no " +
        "dedicated period field, so a null period on an occurrence means it could not be parsed, not " +
        "that there was no period.")]
    public object FindRecurringInvoices(
        [Description("Minimum number of occurrences before a client+product pair counts as recurring. Default 3.")] int minOccurrences = 3)
    {
        var invoices = cache.GetInvoices();
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
        var invoices = cache.GetInvoices();
        var months = RevenueAnalyzer.MonthlyBreakdown(invoices);

        return new
        {
            Months = months.Select(m => new { m.Year, m.Month, m.Net, m.Gross, m.InvoiceCount }),
            AverageMonthlyNet = RevenueAnalyzer.AverageMonthlyNet(months, trailingMonths),
            AverageMonthlyGross = RevenueAnalyzer.AverageMonthlyGross(months, trailingMonths),
            TrailingMonthsUsed = trailingMonths,
        };
    }

    [McpServerTool(Name = "project_budget"), Description(
        "Projects a simple next-year monthly budget from cached invoice history. " +
        "\"flat\" repeats the trailing-12-month average net revenue across every month of the target year. " +
        "\"seasonal\" averages each calendar month's own history separately (so December's budget comes " +
        "from past Decembers, etc.), falling back to the overall average for a month with no history. " +
        "An optional growth rate is applied on top of either method. This is a simple average-based " +
        "projection, not a forecast model - treat it as a first draft budget.")]
    public object ProjectBudget(
        [Description("The year to project a budget for, e.g. 2027.")] int year,
        [Description("\"flat\" or \"seasonal\". Default \"seasonal\".")] string method = "seasonal",
        [Description("Percentage growth/decline to apply on top of the historical average, e.g. 5 for +5%. Default 0.")] decimal growthRatePercent = 0m)
    {
        var invoices = cache.GetInvoices();
        var months = RevenueAnalyzer.MonthlyBreakdown(invoices);

        var projectionMethod = method.Equals("flat", StringComparison.OrdinalIgnoreCase)
            ? ProjectionMethod.FlatTrailingAverage
            : ProjectionMethod.SeasonalMonthlyAverage;

        var projection = BudgetProjector.Project(months, year, projectionMethod, growthRatePercent);

        return new
        {
            projection.Year,
            Method = projection.Method.ToString(),
            projection.GrowthRatePercentApplied,
            Months = projection.Months.Select(m => new { m.Month, m.ProjectedNet }),
            projection.AnnualTotal,
            HistoryMonthsUsed = months.Count,
        };
    }
}
