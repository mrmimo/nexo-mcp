namespace NexoMcp.Core.Models;

public sealed record Invoice(
    int Id,
    string FullNumber,
    DateOnly IssueDate,
    DateOnly? DueDate,
    DocumentKind Kind,
    Direction Direction,
    string ClientSymbol,
    string ClientName,
    string Currency,
    decimal NetTotal,
    decimal GrossTotal,
    decimal VatTotal,
    int? CorrectionOfId,
    string? Notes,
    IReadOnlyList<InvoiceLine> Lines);
