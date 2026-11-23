namespace NexoMcp.Core.Models;

/// <summary>
/// Coarse categorization of a bank/cash operation for cost analysis. "Zus" and "Tax" are
/// recognized by keyword-matching the counterparty name/title (e.g. "ZUS", "Urząd Skarbowy") -
/// see SferaCashFlowReader for why a dedicated structural field from nexo wasn't used instead.
/// "Other" covers every other outflow (supplier payments made this way instead of by invoice,
/// payroll, etc.).
/// </summary>
public enum CashFlowCategory
{
    Zus,
    Tax,
    Other,
}
