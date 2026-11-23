using InsERT.Moria.Dokumenty.Logistyka;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using NexoMcp.Core.Analytics;
using NexoMcp.Core.Models;

namespace NexoMcp.Server.Sfera;

/// <summary>
/// Reads existing sales invoices (DokumentDS) out of nexo through Sfera and maps them onto our
/// own, SDK-independent <see cref="Invoice"/> model.
///
/// STATUS: validated against a real, licensed nexo 62.0.2 instance (see README "Status") - logs
/// in, reads real invoices with correct net/gross/VAT amounts. A couple of fields are still
/// marked "best effort" / "TODO" below where the SDK docs alone weren't conclusive. This is the
/// file where nexo-specific field names live - if something looks wrong against your
/// installation or a different SDK version, this is where to look, and a PR with the correction
/// is very welcome.
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
        // ever issued into memory just to filter a date range. IsInRecycleBin is NOT translatable
        // inside this expression - confirmed live: EF throws NotSupportedException ("not
        // supported in LINQ to Entities") if it's included here, despite being a perfectly normal
        // CLR property by reflection - so it's filtered in memory below instead, after the date
        // range has already been pushed down to SQL.
        IEnumerable<DokumentDS> dokumenty = dokumentySprzedazy.Dane.Wszystkie(
            d => d.DataWydaniaWystawienia >= fromDt && d.DataWydaniaWystawienia <= toDt);

        return dokumenty.Where(IsRealFinalDocument).Select(d => MapDocument(d, Direction.Sale)).ToList();
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

        return dokumenty.Where(IsRealFinalDocument).Select(d => MapDocument(d, Direction.Purchase)).ToList();
    }

    // In memory, after materialization - same as confirmed necessary for IsInRecycleBin (EF
    // threw NotSupportedException when that one was inside the Wszystkie(...) expression tree
    // despite being a perfectly normal CLR property by reflection); applying StatusDokumentu's
    // checks the same way, even though not independently re-verified live for this specific
    // property, avoids risking the same failure again.
    //
    // StatusDokumentu.Uniewazniony/Przejsciowy (confirmed present via reflection on the real SDK)
    // mark a document's *status definition* as voided or a transitional/draft state - a document
    // in either state is not a real, final sale/purchase and must not count as revenue or cost.
    private static bool IsRealFinalDocument(Dokument d)
    {
        if (d.IsInRecycleBin)
            return false;
        if (d.StatusDokumentu is { } status && (status.Uniewazniony || status.Przejsciowy))
            return false;
        return true;
    }

    private static Invoice MapDocument(Dokument d, Direction direction)
    {
        bool isCorrection = d.DokumentKorygowanyId.HasValue;

        // "Towarow" (goods) + "Uslug" (services) are the two confirmed value buckets on a sales
        // document - a document can mix both, so the real total is their sum, not either alone.
        decimal netTotal = d.WartoscTowarowNetto + d.WartoscUslugNetto;
        decimal grossTotal = d.WartoscTowarowBrutto + d.WartoscUslugBrutto;

        Podmiot? klient = d.Podmiot;

        // Confirmed via reflection on the real SDK assembly: Podmiot.Sygnatura.PelnaSygnatura is
        // the client's own human-readable code (the same "PelnaSygnatura" pattern already used
        // for document numbers via NumerWewnetrzny) - prefer it over the internal numeric id,
        // which is still the fallback when a client has no signature set.
        string clientSymbol = klient?.Sygnatura?.PelnaSygnatura is { Length: > 0 } sygnatura
            ? sygnatura
            : klient?.Id.ToString() ?? "NIEZNANY";

        return new Invoice(
            Id: d.Id,
            FullNumber: d.NumerWewnetrzny.PelnaSygnatura,
            IssueDate: DateOnly.FromDateTime(d.DataWydaniaWystawienia.GetValueOrDefault()),
            DueDate: d.Rozrachunek?.TerminPlatnosci is DateTime termin ? DateOnly.FromDateTime(termin) : null,
            Kind: isCorrection ? DocumentKind.Correction : DocumentKind.Invoice,
            Direction: direction,
            ClientSymbol: clientSymbol,
            ClientName: klient?.NazwaSkrocona ?? "(brak danych klienta)",
            ClientTaxId: klient?.NIP,
            Currency: d.Waluta?.Symbol is { Length: > 0 } symbol ? symbol : "PLN",
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

            // A positional fallback ("POZ-1", "POZ-2", ...) used to be used here when there is no
            // catalog symbol at all - but the grouping key recurring-invoice detection uses is
            // (client, product symbol), so that positional fallback silently merged unrelated
            // products that happened to sit in the same line position, and split one real
            // recurring product across different positions on different invoices. Instead,
            // normalize the description itself (period text stripped out) into the fallback key,
            // so two occurrences of "the same" line - differing only in which month they cover -
            // still group together, while two different products do not collide just because
            // neither has a catalog code.
            string productSymbol = !string.IsNullOrWhiteSpace(rawSymbol)
                ? rawSymbol
                : "OPIS:" + RecurringInvoiceDetector.NormalizeDescriptionForGrouping(p.Opis ?? string.Empty);

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
