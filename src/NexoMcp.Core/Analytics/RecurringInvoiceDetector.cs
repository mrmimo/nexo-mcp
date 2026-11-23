using System.Globalization;
using System.Text.RegularExpressions;
using NexoMcp.Core.Models;

namespace NexoMcp.Core.Analytics;

public enum Periodicity
{
    Monthly,
    Quarterly,
    Yearly,
    Irregular,
}

public sealed record RecurringOccurrence(
    int InvoiceId,
    string FullNumber,
    DateOnly IssueDate,
    decimal NetValue,
    DateOnly? ParsedPeriodFrom,
    DateOnly? ParsedPeriodTo);

public sealed record RecurringSeries(
    string ClientSymbol,
    string ClientName,
    string ProductSymbol,
    string ProductDescription,
    Periodicity Periodicity,
    double AverageIntervalDays,
    decimal AverageNetValue,
    IReadOnlyList<RecurringOccurrence> Occurrences);

/// <summary>
/// Groups invoice lines by (client, product) and looks for a regular billing cadence.
/// nexo has no dedicated "billing period" field on a document or line (confirmed against the
/// Sfera data model), so the covered period - when present at all - lives only in the free-text
/// line description; <see cref="TryParsePeriod"/> is a best-effort regex reader of that text for
/// the handful of phrasings commonly used on Polish invoices ("za okres od .. do ..", "za MM.YYYY",
/// "za &lt;miesiąc słownie&gt; YYYY"). It will not catch every wording - treat a null result as
/// "period unknown", not "no period".
/// </summary>
public static class RecurringInvoiceDetector
{
    // Coefficient of variation (stddev / mean) of the gaps between consecutive invoices.
    // Below this, the cadence is considered regular enough to name (Monthly/Quarterly/Yearly)
    // rather than just "Irregular". Confirmed against a real company's production data: a
    // client billed monthly but with the occasional skipped or delayed month (one gap of ~50
    // days among mostly ~30-day ones) is still, for a human looking for recurring clients, a
    // recurring client - a tighter threshold here previously excluded them entirely (see below).
    private const double RegularityThreshold = 0.35;

    public static IReadOnlyList<RecurringSeries> Detect(IEnumerable<Invoice> invoices, int minOccurrences = 3)
    {
        var groups = invoices
            .Where(i => i.Kind == DocumentKind.Invoice)
            .SelectMany(invoice => invoice.Lines.Select(line => (invoice, line)))
            .GroupBy(x => (x.invoice.ClientSymbol, x.line.ProductSymbol));

        var result = new List<RecurringSeries>();

        foreach (var group in groups)
        {
            var ordered = group
                .OrderBy(x => x.invoice.IssueDate)
                .ToList();

            if (ordered.Count < minOccurrences)
                continue;

            var gaps = new List<double>();
            for (int i = 1; i < ordered.Count; i++)
                gaps.Add((ordered[i].invoice.IssueDate.ToDateTime(TimeOnly.MinValue)
                           - ordered[i - 1].invoice.IssueDate.ToDateTime(TimeOnly.MinValue)).TotalDays);

            double meanGap = gaps.Average();
            double variance = gaps.Select(g => Math.Pow(g - meanGap, 2)).Average();
            double stdDev = Math.Sqrt(variance);
            double coefficientOfVariation = meanGap == 0 ? double.MaxValue : stdDev / meanGap;

            // Previously excluded anything that landed on Irregular here - that silently
            // dropped real recurring clients whose billing wasn't perfectly regular. Any
            // client+product pair billed at least minOccurrences times is worth surfacing;
            // Periodicity.Irregular is an honest label on the result, not a reason to hide it.
            var periodicity = coefficientOfVariation > RegularityThreshold
                ? Periodicity.Irregular
                : ClassifyByMeanGap(meanGap);

            var occurrences = ordered.Select(x =>
            {
                var period = TryParsePeriod(x.line.Description, x.invoice.IssueDate);
                return new RecurringOccurrence(
                    x.invoice.Id, x.invoice.FullNumber, x.invoice.IssueDate, x.line.NetValue,
                    period?.From, period?.To);
            }).ToList();

            var (clientSymbol, productSymbol) = group.Key;
            result.Add(new RecurringSeries(
                ClientSymbol: clientSymbol,
                ClientName: ordered[^1].invoice.ClientName,
                ProductSymbol: productSymbol,
                ProductDescription: MostCommonDescription(ordered.Select(x => x.line.Description)),
                Periodicity: periodicity,
                AverageIntervalDays: meanGap,
                AverageNetValue: ordered.Select(x => x.line.NetValue).Average(),
                Occurrences: occurrences));
        }

        return result
            .OrderBy(s => s.ClientName)
            .ThenBy(s => s.ProductSymbol)
            .ToList();
    }

    private static Periodicity ClassifyByMeanGap(double meanGapDays) => meanGapDays switch
    {
        >= 23 and <= 38 => Periodicity.Monthly,
        >= 80 and <= 100 => Periodicity.Quarterly,
        >= 340 and <= 390 => Periodicity.Yearly,
        _ => Periodicity.Irregular,
    };

    private static string MostCommonDescription(IEnumerable<string> descriptions) =>
        descriptions
            .GroupBy(d => d)
            .OrderByDescending(g => g.Count())
            .First().Key;

    private static readonly Regex RangeRegex = new(
        @"okres(?:u|ie)?\s*(?:od\s*)?(\d{1,2}[.\-/]\d{1,2}[.\-/]\d{2,4})\s*(?:do|[-–—])\s*(\d{1,2}[.\-/]\d{1,2}[.\-/]\d{2,4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MonthYearRegex = new(
        @"\bza\s+(\d{1,2})[.\-/](\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] PolishMonths =
    [
        "stycze", "luty", "marz", "kwiecie", "maj", "czerwc",
        "lipiec", "sierpie", "wrzesie", "paździ", "listopad", "grudzie",
    ];

    private static readonly Regex MonthNameYearRegex = new(
        @"\bza\s+([a-ząćęłńóśźż]+)\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Confirmed against this company's own real invoice descriptions: a one-sided "okres do .."
    // with no "od .." start date at all (e.g. "Za okres do 14-listopad 2026",
    // "Za okres do 25-10-2026"). With no start date given, we approximate the period as running
    // from the first of that end date's month - good enough signal for monthly billing, where
    // this phrasing is actually used.
    private static readonly Regex EndDateNumericRegex = new(
        @"okres\s+do\s+(\d{1,2})[.\-/](\d{1,2})[.\-/](\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EndDateMonthNameRegex = new(
        @"okres\s+do\s+(\d{1,2})[-\s]+([a-ząćęłńóśźż]+)\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Best-effort extraction of a billing period from a line's free-text description.
    /// Falls back to null (caller should treat the invoice's issue month as the period instead).
    /// </summary>
    public static (DateOnly From, DateOnly To)? TryParsePeriod(string description, DateOnly issueDate)
    {
        var rangeMatch = RangeRegex.Match(description);
        if (rangeMatch.Success
            && TryParsePolishDate(rangeMatch.Groups[1].Value, out var from)
            && TryParsePolishDate(rangeMatch.Groups[2].Value, out var to))
        {
            return (from, to);
        }

        var endDateNumericMatch = EndDateNumericRegex.Match(description);
        if (endDateNumericMatch.Success
            && int.TryParse(endDateNumericMatch.Groups[1].Value, out var endDay)
            && int.TryParse(endDateNumericMatch.Groups[2].Value, out var endMonth)
            && int.TryParse(endDateNumericMatch.Groups[3].Value, out var endYear)
            && endMonth is >= 1 and <= 12)
        {
            var end = new DateOnly(endYear, endMonth, endDay);
            return (new DateOnly(end.Year, end.Month, 1), end);
        }

        var endDateMonthNameMatch = EndDateMonthNameRegex.Match(description);
        if (endDateMonthNameMatch.Success
            && int.TryParse(endDateMonthNameMatch.Groups[1].Value, out var endDay2)
            && int.TryParse(endDateMonthNameMatch.Groups[3].Value, out var endYear2))
        {
            var stem = endDateMonthNameMatch.Groups[2].Value.ToLowerInvariant();
            var monthIndex = Array.FindIndex(PolishMonths, m => stem.StartsWith(m, StringComparison.Ordinal));
            if (monthIndex >= 0)
            {
                var end = new DateOnly(endYear2, monthIndex + 1, endDay2);
                return (new DateOnly(end.Year, end.Month, 1), end);
            }
        }

        var monthYearMatch = MonthYearRegex.Match(description);
        if (monthYearMatch.Success
            && int.TryParse(monthYearMatch.Groups[1].Value, out var month)
            && int.TryParse(monthYearMatch.Groups[2].Value, out var year)
            && month is >= 1 and <= 12)
        {
            var first = new DateOnly(year, month, 1);
            return (first, first.AddMonths(1).AddDays(-1));
        }

        var monthNameMatch = MonthNameYearRegex.Match(description);
        if (monthNameMatch.Success && int.TryParse(monthNameMatch.Groups[2].Value, out var namedYear))
        {
            var stem = monthNameMatch.Groups[1].Value.ToLowerInvariant();
            var monthIndex = Array.FindIndex(PolishMonths, m => stem.StartsWith(m, StringComparison.Ordinal));
            if (monthIndex >= 0)
            {
                var first = new DateOnly(namedYear, monthIndex + 1, 1);
                return (first, first.AddMonths(1).AddDays(-1));
            }
        }

        return null;
    }

    private static bool TryParsePolishDate(string text, out DateOnly result)
    {
        string[] formats = ["dd.MM.yyyy", "dd-MM-yyyy", "dd/MM/yyyy", "d.M.yyyy", "dd.MM.yy"];
        foreach (var format in formats)
        {
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                return true;
        }
        result = default;
        return false;
    }
}
