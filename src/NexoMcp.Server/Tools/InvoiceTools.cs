using System.ComponentModel;
using ModelContextProtocol.Server;
using NexoMcp.Core.Cache;

namespace NexoMcp.Server.Tools;

[McpServerToolType]
public sealed class InvoiceTools(InvoiceCache cache)
{
    [McpServerTool(Name = "list_invoices"), Description(
        "Lists invoices from the local cache, optionally filtered by issue date range and/or client symbol. " +
        "Returns headers only (no line items) - use get_invoice for the full detail of one invoice.")]
    public object ListInvoices(
        [Description("ISO date (yyyy-MM-dd), inclusive. Omit for no lower bound.")] string? from = null,
        [Description("ISO date (yyyy-MM-dd), inclusive. Omit for no upper bound.")] string? to = null,
        [Description("Client symbol to filter by. Omit to include all clients.")] string? clientSymbol = null)
    {
        var invoices = cache.GetInvoices(
            from is null ? null : DateOnly.Parse(from),
            to is null ? null : DateOnly.Parse(to),
            clientSymbol);

        return invoices.Select(i => new
        {
            i.Id,
            i.FullNumber,
            IssueDate = i.IssueDate.ToString("yyyy-MM-dd"),
            i.Kind,
            i.ClientSymbol,
            i.ClientName,
            i.Currency,
            i.NetTotal,
            i.GrossTotal,
            LineCount = i.Lines.Count,
        });
    }

    [McpServerTool(Name = "get_invoice"), Description(
        "Gets one invoice from the local cache by its nexo document id, including every line item " +
        "(product, description, quantity, unit price, net/gross value).")]
    public object? GetInvoice([Description("The nexo document id, as returned by list_invoices.")] int invoiceId)
    {
        var invoice = cache.GetInvoices().FirstOrDefault(i => i.Id == invoiceId);
        if (invoice is null)
            return null;

        return new
        {
            invoice.Id,
            invoice.FullNumber,
            IssueDate = invoice.IssueDate.ToString("yyyy-MM-dd"),
            DueDate = invoice.DueDate?.ToString("yyyy-MM-dd"),
            invoice.Kind,
            invoice.ClientSymbol,
            invoice.ClientName,
            invoice.Currency,
            invoice.NetTotal,
            invoice.GrossTotal,
            invoice.VatTotal,
            invoice.CorrectionOfId,
            invoice.Notes,
            Lines = invoice.Lines.Select(l => new
            {
                l.LineNo,
                l.ProductSymbol,
                l.Description,
                l.Quantity,
                l.Unit,
                l.UnitNetPrice,
                l.VatRate,
                l.NetValue,
                l.GrossValue,
            }),
        };
    }
}
