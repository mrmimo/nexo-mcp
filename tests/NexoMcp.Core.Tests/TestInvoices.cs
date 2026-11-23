using NexoMcp.Core.Models;

namespace NexoMcp.Core.Tests;

internal static class TestInvoices
{
    public static Invoice Invoice(
        int id,
        DateOnly issueDate,
        string clientSymbol,
        decimal net,
        DocumentKind kind = DocumentKind.Invoice,
        IReadOnlyList<InvoiceLine>? lines = null,
        Direction direction = Direction.Sale) =>
        new(
            id,
            $"FV {id}/{issueDate.Year}",
            issueDate,
            issueDate.AddDays(14),
            kind,
            direction,
            clientSymbol,
            $"Klient {clientSymbol}",
            null,
            "PLN",
            net,
            Math.Round(net * 1.23m, 2),
            Math.Round(net * 0.23m, 2),
            null,
            null,
            lines ?? [Line(1, "HOSTING", "Usluga hostingu", net)]);

    public static InvoiceLine Line(int lineNo, string productSymbol, string description, decimal net) =>
        new(lineNo, productSymbol, description, 1, "szt", net, 23, net, Math.Round(net * 1.23m, 2));
}
