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
/// </summary>
public static class SferaCashFlowReader
{
    public static List<CashOperation> ReadBankOperations(Uchwyt sfera, DateOnly? from = null, DateOnly? to = null)
    {
        IOperacjeBankowe operacjeBankowe = sfera.OperacjeBankowe();

        DateTime fromDt = (from ?? DateOnly.MinValue).ToDateTime(TimeOnly.MinValue);
        DateTime toDt = (to ?? DateOnly.MaxValue).ToDateTime(TimeOnly.MaxValue);

        IEnumerable<OperacjaBankowa> operacje = operacjeBankowe.Dane.Wszystkie(
            o => o.Data >= fromDt && o.Data <= toDt);

        return operacje.Select(MapBankOperation).ToList();
    }

    public static List<CashOperation> ReadCashOperations(Uchwyt sfera, DateOnly? from = null, DateOnly? to = null)
    {
        IOperacjeKasowe operacjeKasowe = sfera.OperacjeKasowe();

        DateTime fromDt = (from ?? DateOnly.MinValue).ToDateTime(TimeOnly.MinValue);
        DateTime toDt = (to ?? DateOnly.MaxValue).ToDateTime(TimeOnly.MaxValue);

        IEnumerable<OperacjaKasowa> operacje = operacjeKasowe.Dane.Wszystkie(
            o => o.Data >= fromDt && o.Data <= toDt);

        return operacje.Select(MapCashOperation).ToList();
    }

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
        if (haystack.Contains("zus", StringComparison.OrdinalIgnoreCase))
            return CashFlowCategory.Zus;
        if (haystack.Contains("skarbow", StringComparison.OrdinalIgnoreCase) || haystack.Contains("podat", StringComparison.OrdinalIgnoreCase))
            return CashFlowCategory.Tax;
        return CashFlowCategory.Other;
    }
}
