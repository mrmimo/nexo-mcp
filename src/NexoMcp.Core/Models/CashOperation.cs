namespace NexoMcp.Core.Models;

/// <summary>
/// A bank or cash-register movement - the source for costs that never show up as a purchase
/// invoice (ZUS, tax office transfers, payroll, etc.). <see cref="MoneyIn"/> false means an
/// outflow (a cost); true means an inflow (e.g. a client paying by bank transfer, which is not
/// counted as revenue here - revenue comes from invoices, this would double count it).
/// </summary>
public sealed record CashOperation(
    int Id,
    DateOnly Date,
    decimal Amount,
    bool MoneyIn,
    string Title,
    string CounterpartyName,
    CashFlowCategory Category,
    string Source);
