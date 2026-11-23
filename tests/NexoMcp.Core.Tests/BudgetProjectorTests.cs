using NexoMcp.Core.Analytics;
using Xunit;

namespace NexoMcp.Core.Tests;

public class BudgetProjectorTests
{
    [Fact]
    public void Flat_projection_repeats_trailing_average_across_all_months()
    {
        var history = Enumerable.Range(1, 12)
            .Select(m => new MonthlyRevenue(2025, m, 1200m, 1476m, 1))
            .ToList();

        var budget = BudgetProjector.Project(history, 2026, ProjectionMethod.FlatTrailingAverage);

        Assert.Equal(12, budget.Months.Count);
        Assert.All(budget.Months, m => Assert.Equal(1200m, m.ProjectedNet));
        Assert.Equal(14400m, budget.AnnualTotal);
    }

    [Fact]
    public void Flat_projection_applies_growth_rate()
    {
        var history = Enumerable.Range(1, 12)
            .Select(m => new MonthlyRevenue(2025, m, 1000m, 1230m, 1))
            .ToList();

        var budget = BudgetProjector.Project(history, 2026, ProjectionMethod.FlatTrailingAverage, growthRatePercent: 10m);

        Assert.All(budget.Months, m => Assert.Equal(1100m, m.ProjectedNet));
    }

    [Fact]
    public void Seasonal_projection_uses_each_calendar_month_own_history()
    {
        var history = new List<MonthlyRevenue>
        {
            new(2024, 12, 5000m, 6150m, 1), // December: high season
            new(2025, 1, 500m, 615m, 1),
            new(2025, 2, 500m, 615m, 1),
        };

        var budget = BudgetProjector.Project(history, 2026, ProjectionMethod.SeasonalMonthlyAverage);

        Assert.Equal(5000m, budget.Months.Single(m => m.Month == 12).ProjectedNet);
        Assert.Equal(500m, budget.Months.Single(m => m.Month == 1).ProjectedNet);
        // March has no history at all - falls back to the overall average.
        var overallAverage = history.Average(h => h.Net);
        Assert.Equal(overallAverage, budget.Months.Single(m => m.Month == 3).ProjectedNet);
    }

    [Fact]
    public void Empty_history_projects_zero_without_throwing()
    {
        var budget = BudgetProjector.Project([], 2026, ProjectionMethod.FlatTrailingAverage);

        Assert.Equal(0m, budget.AnnualTotal);
    }
}
