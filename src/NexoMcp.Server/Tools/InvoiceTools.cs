using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using NexoMcp.Core.Cache;
using NexoMcp.Core.Models;

namespace NexoMcp.Server.Tools;

[McpServerToolType]
public sealed class InvoiceTools(NexoCache cache)
{
    [McpServerTool(Name = "list_invoices"), Description(
        "Lista faktur z lokalnego cache, opcjonalnie filtrowana po zakresie dat wystawienia, " +
        "symbolu klienta/dostawcy i kierunku (\"Sale\" dla faktur sprzedazy, \"Purchase\" dla " +
        "faktur zakupu/kosztow - pomin, aby pokazac obie). Zwraca tylko naglowki (bez pozycji) " +
        "- uzyj get_invoice po pelny szczegol jednej faktury.")]
    public object ListInvoices(
        [Description("Data ISO (rrrr-MM-dd), wlacznie. Pomin dla braku dolnej granicy.")] string? from = null,
        [Description("Data ISO (rrrr-MM-dd), wlacznie. Pomin dla braku gornej granicy.")] string? to = null,
        [Description("Symbol klienta/dostawcy do filtrowania. Pomin, aby uwzglednic wszystkich.")] string? clientSymbol = null,
        [Description("\"Sale\" albo \"Purchase\". Pomin, aby uwzglednic obie.")] string? direction = null)
    {
        Direction? parsedDirection = null;
        if (direction is not null)
        {
            if (!Enum.TryParse(direction, ignoreCase: true, out Direction d))
                throw new ArgumentException($"Nieznana wartosc direction: \"{direction}\". Oczekiwano \"Sale\" albo \"Purchase\".");
            parsedDirection = d;
        }

        var invoices = cache.GetInvoices(
            ParseIsoDateOrNull(from, nameof(from)),
            ParseIsoDateOrNull(to, nameof(to)),
            clientSymbol,
            direction: parsedDirection);

        return invoices.Select(i => new
        {
            i.Id,
            i.FullNumber,
            IssueDate = i.IssueDate.ToString("yyyy-MM-dd"),
            Kind = i.Kind.ToString(),
            Direction = i.Direction.ToString(),
            i.ClientSymbol,
            i.ClientName,
            i.Currency,
            i.NetTotal,
            i.GrossTotal,
            LineCount = i.Lines.Count,
        });
    }

    [McpServerTool(Name = "get_invoice"), Description(
        "Pelny szczegol jednej faktury z lokalnego cache po jej id dokumentu nexo, z kazda pozycja " +
        "(produkt, opis, ilosc, cena jednostkowa, wartosc netto/brutto).")]
    public object? GetInvoice([Description("Id dokumentu nexo, zwracane przez list_invoices.")] int invoiceId)
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
            Kind = invoice.Kind.ToString(),
            Direction = invoice.Direction.ToString(),
            invoice.ClientSymbol,
            invoice.ClientName,
            invoice.ClientTaxId,
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

    private static DateOnly? ParseIsoDateOrNull(string? value, string paramName)
    {
        if (value is null)
            return null;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new ArgumentException($"{paramName} musi byc data w formacie rrrr-MM-dd, otrzymano: \"{value}\".");
        return parsed;
    }
}
