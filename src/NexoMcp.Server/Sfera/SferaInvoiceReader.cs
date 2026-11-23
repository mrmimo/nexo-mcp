using InsERT.Moria.Dokumenty.Logistyka;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using NexoMcp.Core.Models;

namespace NexoMcp.Server.Sfera;

/// <summary>
/// Reads existing sales invoices (DokumentDS) out of nexo through Sfera and maps them onto our
/// own, SDK-independent <see cref="Invoice"/> model.
///
/// STATUS: compiles against the real nexo SDK 62.0.2 assemblies (InsERT.Moria.*), but has not
/// yet been run against a live, licensed nexo instance - no such instance was available while
/// writing it, only the SDK's own XML API docs and DLLs. A couple of fields (marked "best
/// effort" / "TODO" in the comments below) could not be pinned down from the docs alone and may
/// need a one-line fix. This is the file where nexo-specific field names live - if something
/// looks wrong against your installation or a different SDK version, this is where to look, and
/// a PR with the correction is very welcome.
/// </summary>
public static class SferaInvoiceReader
{
    public static List<Invoice> ReadInvoices(Uchwyt sfera, DateOnly? from = null, DateOnly? to = null)
    {
        IDokumentySprzedazy dokumentySprzedazy = sfera.DokumentySprzedazy();

        DateTime fromDt = (from ?? DateOnly.MinValue).ToDateTime(TimeOnly.MinValue);
        DateTime toDt = (to ?? DateOnly.MaxValue).ToDateTime(TimeOnly.MaxValue);

        // Pushed down to SQL by Sfera's EF-backed Dane collection (confirmed: Wszystkie<T> takes
        // an Expression<Func<T,bool>>, not a plain delegate) - this does not load every invoice
        // ever issued into memory just to filter a date range.
        IEnumerable<DokumentDS> dokumenty = dokumentySprzedazy.Dane.Wszystkie(
            d => d.DataWydaniaWystawienia >= fromDt && d.DataWydaniaWystawienia <= toDt);

        return dokumenty.Select(d => MapDocument(d, Direction.Sale)).ToList();
    }

    /// <summary>
    /// Supplier/purchase documents - a real operating cost, as opposed to ZUS/tax payments which
    /// never have a document at all (see SferaCashFlowReader). DokumentDZ shares the exact same
    /// base class (Dokument) and field names as the sales-side DokumentDS, confirmed via
    /// reflection on the real SDK assembly - one mapping function covers both.
    /// </summary>
    public static List<Invoice> ReadPurchaseInvoices(Uchwyt sfera, DateOnly? from = null, DateOnly? to = null)
    {
        IDokumentyZakupu dokumentyZakupu = sfera.DokumentyZakupu();

        DateTime fromDt = (from ?? DateOnly.MinValue).ToDateTime(TimeOnly.MinValue);
        DateTime toDt = (to ?? DateOnly.MaxValue).ToDateTime(TimeOnly.MaxValue);

        IEnumerable<DokumentDZ> dokumenty = dokumentyZakupu.Dane.Wszystkie(
            d => d.DataWydaniaWystawienia >= fromDt && d.DataWydaniaWystawienia <= toDt);

        return dokumenty.Select(d => MapDocument(d, Direction.Purchase)).ToList();
    }

    private static Invoice MapDocument(Dokument d, Direction direction)
    {
        bool isCorrection = d.DokumentKorygowanyId.HasValue;

        // "Towarow" (goods) + "Uslug" (services) are the two confirmed value buckets on a sales
        // document - a document can mix both, so the real total is their sum, not either alone.
        decimal netTotal = d.WartoscTowarowNetto + d.WartoscUslugNetto;
        decimal grossTotal = d.WartoscTowarowBrutto + d.WartoscUslugBrutto;

        Podmiot? klient = d.Podmiot;

        return new Invoice(
            Id: d.Id,
            FullNumber: d.NumerWewnetrzny.PelnaSygnatura,
            IssueDate: DateOnly.FromDateTime(d.DataWydaniaWystawienia.GetValueOrDefault()),
            DueDate: d.Rozrachunek?.TerminPlatnosci is DateTime termin ? DateOnly.FromDateTime(termin) : null,
            Kind: isCorrection ? DocumentKind.Correction : DocumentKind.Invoice,
            Direction: direction,
            ClientSymbol: klient?.Id.ToString() ?? "NIEZNANY",
            ClientName: klient?.NazwaSkrocona ?? "(brak danych klienta)",
            Currency: "PLN", // TODO: map from d.Waluta once its code/symbol property is confirmed.
            NetTotal: netTotal,
            GrossTotal: grossTotal,
            VatTotal: grossTotal - netTotal,
            CorrectionOfId: d.DokumentKorygowanyId,
            Notes: d.Uwagi,
            Lines: MapLines(d));
    }

    private static List<InvoiceLine> MapLines(Dokument d)
    {
        var lines = new List<InvoiceLine>();
        int lineNo = 1;
        foreach (PozycjaDokumentu p in d.Pozycje)
        {
            // Wartosc is nexo's own money-with-discount-and-VAT value type (confirmed fields:
            // NettoPoRabacie/BruttoPoRabacie/VatPoRabacie = after any line discount, which is
            // what actually lands on the invoice) - not a plain decimal.
            decimal netValue = p.Wartosc.NettoPoRabacie;
            decimal grossValue = p.Wartosc.BruttoPoRabacie;
            decimal vatValue = p.Wartosc.VatPoRabacie;
            decimal vatRate = netValue == 0 ? 0 : Math.Round(vatValue / netValue * 100m, 0);

            decimal quantity = p.Ilosc;
            decimal unitNetPrice = quantity == 0 ? 0 : netValue / quantity;

            // Confirmed against real data: a line's Symbol can come back as "" (not null) - e.g.
            // generic/recurring service lines with no catalog code - which would otherwise skip
            // the fallback below via plain ?? chaining.
            string? rawSymbol = p.AsortymentAktualny?.Symbol;
            if (string.IsNullOrWhiteSpace(rawSymbol))
                rawSymbol = p.AsortymentWybrany?.Symbol;
            string productSymbol = string.IsNullOrWhiteSpace(rawSymbol) ? $"POZ-{lineNo}" : rawSymbol;

            lines.Add(new InvoiceLine(
                LineNo: lineNo,
                ProductSymbol: productSymbol,
                Description: p.Opis ?? string.Empty,
                Quantity: quantity,
                Unit: p.JednostkaMiaryAs?.ToString() ?? string.Empty,
                UnitNetPrice: unitNetPrice,
                VatRate: vatRate,
                NetValue: netValue,
                GrossValue: grossValue));

            lineNo++;
        }
        return lines;
    }
}
