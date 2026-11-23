namespace NexoMcp.Core.Models;

public sealed record InvoiceLine(
    int LineNo,
    string ProductSymbol,
    string Description,
    decimal Quantity,
    string Unit,
    decimal UnitNetPrice,
    decimal VatRate,
    decimal NetValue,
    decimal GrossValue);
