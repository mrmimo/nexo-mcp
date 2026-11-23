using InsERT.Moria.Bank;
using InsERT.Moria.Kasa;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using NexoMcp.Core.Models;

namespace NexoMcp.Server.Sfera;

/// <summary>
/// Reads bank and cash-register operations - the source for costs that never show up as a
/// purchase invoice (ZUS, tax office transfers, payroll paid straight from the till, etc.).
/// See SferaInvoiceReader's STATUS note: compiled against the real SDK.
///
/// ZUS/tax categorization was first tried against RodzajOperacjiBankowej's own dedicated
/// ParametryDlaPrzelewuZUS/Podatku collections, which looked like a clean structural signal from
/// the SDK docs - but testing against a real company database showed every tax-office transfer
/// coming back as "Zus" too, so those collections do not mean what the docs suggested (or are
/// populated for reasons unrelated to the actual transfer's purpose). Replaced with keyword
/// matching on the counterparty name instead, which the real data confirms is reliable: tax and
/// ZUS payments go to a small, recognizable set of counterparty names ("Urząd Skarbowy ...",
/// "Centrala ZUS ..."). Cash-register operations use the same matching (no structural signal
/// exists there at all either way).
///
/// CRITICAL: an operation must never reach <see cref="NexoMcp.Core.Analytics.ExpenseAnalyzer"/>
/// if it would double-count a cost already counted elsewhere. Confirmed by reflection on the
/// real SDK (PrzeplywFinansowy, the base class shared by OperacjaBankowa/OperacjaKasowa):
///  - PlatnoscDokumentu non-null: this operation *settles a document* (a purchase invoice we
///    already count on an accrual basis) - counting the payment too would double the cost.
///  - ElementTransferu non-null, or TypCGFTransferu set: an internal transfer between the
///    company's own accounts/collection points, not a cost to anyone.
///  - OperacjaBankowa.TypTransferuVAT set: a split-payment VAT-account transfer. This removes
///    VAT remittances made *via the split-payment mechanism* from the "Tax" category - VAT is
///    collected from clients and passed through, not a real cost, while revenue here is already
///    counted net of VAT. This field is only set for split-payment transfers specifically, so a
///    VAT remittance paid as an ordinary transfer from the main account would NOT be caught by
///    it alone - see the title-text check below for that case. An income-tax (PIT/CIT) transfer
///    to the same tax office has neither signal set and is correctly kept as a real cost.
///  - Title contains "/VAT/" (the split-payment reference format actually observed on live
///    data, e.g. "/VAT/51,83/IDC/..."): confirmed live to be the split-payment (MPP) message a
///    bank puts on a transfer paying a *supplier's* invoice, not a remittance to the tax office
///    (IDC in that format is the supplier's NIP) - TypTransferuVAT alone does not catch this
///    because it is a transfer to a normal counterparty, not from a split-payment VAT sub-
///    account. Still correctly excluded here: it funds an invoice already counted as a cost on
///    an accrual basis (PlatnoscDokumentu/Rozrachunek permitting - see "Znane ograniczenia"),
///    not a second real cost.
///  - Title matches "/SFP/VAT" or "/SFP/JPK_V7" (the tax-form code nexo's bank puts after /SFP/
///    in a tax-office transfer title - confirmed live against real transfers using this exact
///    "/TI/.../OKR/.../SFP/&lt;forma&gt;/TXT/..." shape for PIT4/CIT/PIT, which are real costs and
///    correctly left in "Tax"): an ordinary (non-split-payment) VAT-7/JPK_V7 declaration payment
///    to the tax office. Not observed live on this project's own data (this company evidently
///    pays VAT only through the split-payment mechanism above), so this specific pattern is
///    unverified against a real example - but VAT collected from clients and already excluded
///    from revenue (counted net) must not become a cost here either, whichever way it is paid.
/// </summary>
public static class SferaCashFlowReader
{
    public static List<CashOperation> ReadBankOperations(Uchwyt sfera, DateOnly? from = null, DateOnly? to = null)
    {
        IOperacjeBankowe operacjeBankowe = sfera.OperacjeBankowe();

        DateTime fromDt = (from ?? DateOnly.MinValue).ToDateTime(TimeOnly.MinValue);
        DateTime toDt = (to ?? DateOnly.MaxValue).ToDateTime(TimeOnly.MaxValue);

        // IsInRecycleBin is NOT translatable inside this expression - confirmed live: EF throws
        // NotSupportedException if it's included here, despite being a perfectly normal CLR
        // property by reflection - so it's filtered in memory below instead.
        IEnumerable<OperacjaBankowa> operacje = operacjeBankowe.Dane.Wszystkie(
            o => o.Data >= fromDt && o.Data <= toDt);

        return operacje
            .Where(o => !o.IsInRecycleBin && !IsSettlementOrInternalTransfer(o)
                        && o.TypTransferuVAT is null && !LooksLikeVatRemittance(o.Tytul))
            .Select(MapBankOperation)
            .ToList();
    }

    public static List<CashOperation> ReadCashOperations(Uchwyt sfera, DateOnly? from = null, DateOnly? to = null)
    {
        IOperacjeKasowe operacjeKasowe = sfera.OperacjeKasowe();

        DateTime fromDt = (from ?? DateOnly.MinValue).ToDateTime(TimeOnly.MinValue);
        DateTime toDt = (to ?? DateOnly.MaxValue).ToDateTime(TimeOnly.MaxValue);

        IEnumerable<OperacjaKasowa> operacje = operacjeKasowe.Dane.Wszystkie(
            o => o.Data >= fromDt && o.Data <= toDt);

        return operacje
            .Where(o => !o.IsInRecycleBin && !IsSettlementOrInternalTransfer(o))
            .Select(MapCashOperation)
            .ToList();
    }

    private static bool IsSettlementOrInternalTransfer(PrzeplywFinansowy o) =>
        o.PlatnoscDokumentu is not null || o.ElementTransferu is not null || o.TypCGFTransferu is not null;

    // "/VAT/51,83/IDC/..." is the structured reference nexo/banks put on a split-payment (MPP)
    // transfer's title - confirmed against real data. Deliberately narrow (slashes on both
    // sides) rather than a bare "vat" substring: the word "VAT" shows up constantly in ordinary
    // Polish invoice/payment titles ("Zapłata za fakturę VAT nr ...") that are real costs, not
    // VAT remittances - a bare substring match would wrongly exclude those too.
    //
    // "/SFP/VAT.../TXT/" or "/SFP/JPK_V7.../TXT/" is the same tax-transfer title shape confirmed
    // live for PIT4/CIT/PIT ("/TI/.../OKR/.../SFP/PIT4/TXT/pit 4") applied to a VAT declaration
    // instead - a real pattern for this title format, even though no live example of the VAT
    // variant itself has been seen on this project's own data (see class doc comment).
    private static readonly System.Text.RegularExpressions.Regex SfpVatPattern =
        new(@"/SFP/\s*(VAT|JPK_V7)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool LooksLikeVatRemittance(string? title) =>
        title is not null && (title.Contains("/VAT/", StringComparison.OrdinalIgnoreCase) || SfpVatPattern.IsMatch(title));

    private static CashOperation MapBankOperation(OperacjaBankowa o)
    {
        string counterparty = o.Kontrahent?.NazwaSkrocona ?? o.NazwaKontrahentaZaimportowanego ?? string.Empty;

        return new CashOperation(
            Id: o.Id,
            Date: DateOnly.FromDateTime(o.Data),
            Amount: o.Kwota,
            MoneyIn: o.Wplyw,
            Title: o.Tytul ?? string.Empty,
            CounterpartyName: counterparty,
            Category: Categorize(counterparty, o.Tytul),
            Source: "Bank");
    }

    private static CashOperation MapCashOperation(OperacjaKasowa o)
    {
        string counterparty = o.Podmiot?.NazwaSkrocona ?? string.Empty;
        string title = o.Opis ?? o.Tytul ?? string.Empty;

        return new CashOperation(
            Id: o.Id,
            Date: DateOnly.FromDateTime(o.Data),
            Amount: o.Kwota,
            MoneyIn: o.Wplyw,
            Title: title,
            CounterpartyName: counterparty,
            Category: Categorize(counterparty, title),
            Source: "Kasa");
    }

    private static CashFlowCategory Categorize(string counterpartyName, string? title)
    {
        string haystack = $"{counterpartyName} {title}";
        if (ContainsWord(haystack, "zus"))
            return CashFlowCategory.Zus;
        if (ContainsWord(haystack, "skarbow") || ContainsWord(haystack, "podat"))
            return CashFlowCategory.Tax;
        return CashFlowCategory.Other;
    }

    // Word-boundary match, not a raw substring search: "zus" must not match inside an unrelated
    // word like "kazus" ("casus"/legal case) - confirmed as a real false-positive risk, not a
    // hypothetical one, since this still only matches by text, same caveat as period parsing.
    private static bool ContainsWord(string haystack, string word) =>
        System.Text.RegularExpressions.Regex.IsMatch(haystack, $@"\b{word}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
