namespace NexoMcp.Core.Analytics;

/// <summary>
/// One calendar month's total of some money series - revenue, costs, whatever the caller
/// aggregated. Shared shape so <see cref="BudgetProjector"/> works the same way for either.
/// </summary>
public sealed record MonthlyAmount(int Year, int Month, decimal Net, decimal Gross, int Count);
