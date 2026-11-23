namespace NexoMcp.Core.Analytics;

public enum ProjectionMethod
{
    /// <summary>Trailing-12-month (or all available history, if shorter) average, repeated flat across every month of the target year.</summary>
    FlatTrailingAverage,

    /// <summary>Average of each calendar month's own history (all Januaries, all Februaries, ...), to carry seasonality into the projection. Falls back to the overall average for a month with no history.</summary>
    SeasonalMonthlyAverage,
}

public sealed record MonthlyBudget(int Month, decimal ProjectedNet);

public sealed record BudgetProjection(
    int Year,
    ProjectionMethod Method,
    decimal GrowthRatePercentApplied,
    IReadOnlyList<MonthlyBudget> Months,
    decimal AnnualTotal);

/// <summary>
/// Turns historical monthly revenue into a simple next-year budget. Intentionally simple: a flat
/// or seasonal average with an optional manual growth-rate override, not a forecasting model -
/// good enough for a first budget draft, not a substitute for judgement.
/// </summary>
public static class BudgetProjector
{
    public static BudgetProjection Project(
        IReadOnlyList<MonthlyRevenue> history,
        int targetYear,
        ProjectionMethod method,
        decimal growthRatePercent = 0m)
    {
        var months = method switch
        {
            ProjectionMethod.FlatTrailingAverage => ProjectFlat(history, growthRatePercent),
            ProjectionMethod.SeasonalMonthlyAverage => ProjectSeasonal(history, growthRatePercent),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

        return new BudgetProjection(targetYear, method, growthRatePercent, months, months.Sum(m => m.ProjectedNet));
    }

    private static List<MonthlyBudget> ProjectFlat(IReadOnlyList<MonthlyRevenue> history, decimal growthRatePercent)
    {
        var trailing = history.TakeLast(12).ToList();
        decimal baseAverage = trailing.Count == 0 ? 0m : trailing.Average(m => m.Net);
        decimal projected = ApplyGrowth(baseAverage, growthRatePercent);

        return Enumerable.Range(1, 12).Select(month => new MonthlyBudget(month, projected)).ToList();
    }

    private static List<MonthlyBudget> ProjectSeasonal(IReadOnlyList<MonthlyRevenue> history, decimal growthRatePercent)
    {
        decimal overallAverage = history.Count == 0 ? 0m : history.Average(m => m.Net);

        var result = new List<MonthlyBudget>(12);
        for (int month = 1; month <= 12; month++)
        {
            var sameMonth = history.Where(m => m.Month == month).ToList();
            decimal baseValue = sameMonth.Count > 0 ? sameMonth.Average(m => m.Net) : overallAverage;
            result.Add(new MonthlyBudget(month, ApplyGrowth(baseValue, growthRatePercent)));
        }
        return result;
    }

    private static decimal ApplyGrowth(decimal baseValue, decimal growthRatePercent) =>
        baseValue * (1 + growthRatePercent / 100m);
}
