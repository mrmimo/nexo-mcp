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
        // A gap between invoices needs at least 2 data points to exist at all - below that,
        // "periodicity" is meaningless. A caller passing minOccurrences <= 1 does not get a
        // crash from a single-element group; it just can't produce anything below 2.
        int effectiveMinOccurrences = Math.Max(2, minOccurrences);

        var groups = invoices
            .Where(i => i.Kind == DocumentKind.Invoice)
            .SelectMany(invoice => invoice.Lines.Select(line => (invoice, line)))
            .GroupBy(x => (x.invoice.ClientSymbol, x.line.ProductSymbol));

        var result = new List<RecurringSeries>();

        foreach (var group in groups)
        {
            // Collapse multiple lines of the same product on the *same* invoice into one
            // occurrence (summing their value) - otherwise one invoice that happens to list the
            // same product three times would be counted as three separate billing events.
            var perInvoice = group
                .GroupBy(x => x.invoice.Id)
                .Select(invoiceGroup =>
                {
                    var first = invoiceGroup.First();
                    return (
                        invoice: first.invoice,
                        description: first.line.Description,
                        netValue: invoiceGroup.Sum(x => x.line.NetValue));
                })
                .OrderBy(x => x.invoice.IssueDate)
                .ToList();

            if (perInvoice.Count < effectiveMinOccurrences)
                continue;

            var gaps = new List<double>();
            for (int i = 1; i < perInvoice.Count; i++)
                gaps.Add((perInvoice[i].invoice.IssueDate.ToDateTime(TimeOnly.MinValue)
                           - perInvoice[i - 1].invoice.IssueDate.ToDateTime(TimeOnly.MinValue)).TotalDays);

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

            var occurrences = perInvoice.Select(x =>
            {
                var period = TryParsePeriod(x.description, x.invoice.IssueDate);
                return new RecurringOccurrence(
                    x.invoice.Id, x.invoice.FullNumber, x.invoice.IssueDate, x.netValue,
                    period?.From, period?.To);
            }).ToList();

            var (clientSymbol, productSymbol) = group.Key;
            result.Add(new RecurringSeries(
                ClientSymbol: clientSymbol,
                ClientName: perInvoice[^1].invoice.ClientName,
                ProductSymbol: productSymbol,
                ProductDescription: MostCommonDescription(perInvoice.Select(x => x.description)),
                Periodicity: periodicity,
                AverageIntervalDays: meanGap,
                AverageNetValue: perInvoice.Average(x => x.netValue),
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

    // Exact word forms (not prefixes) for both the nominative/accusative ("styczeń") and
    // genitive ("stycznia") case - Polish uses the genitive after a day number ("do 14
    // stycznia"), and the accusative (identical to the nominative for these masculine nouns) in
    // "za <miesiąc> <rok>" - so both forms genuinely occur depending on phrasing, and a prefix
    // match on only the nominative form missed most genitive ones (confirmed: a naive
    // nominative-prefix match against genitive forms failed for 8 of 12 months). Exact matching
    // also closes a real false-positive: a prefix match on "maj" (May) matched the unrelated
    // word "majątek" ("assets") in "za majątek 2026".
    private static readonly Dictionary<string, int> PolishMonthForms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["styczeń"] = 1, ["stycznia"] = 1,
        ["luty"] = 2, ["lutego"] = 2,
        ["marzec"] = 3, ["marca"] = 3,
        ["kwiecień"] = 4, ["kwietnia"] = 4,
        ["maj"] = 5, ["maja"] = 5,
        ["czerwiec"] = 6, ["czerwca"] = 6,
        ["lipiec"] = 7, ["lipca"] = 7,
        ["sierpień"] = 8, ["sierpnia"] = 8,
        ["wrzesień"] = 9, ["września"] = 9,
        ["październik"] = 10, ["października"] = 10,
        ["listopad"] = 11, ["listopada"] = 11,
        ["grudzień"] = 12, ["grudnia"] = 12,
    };

    private static readonly Regex MonthNameYearRegex = new(
        @"\bza\s+([a-ząćęłńóśźż]+)\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Confirmed against real production invoice descriptions: a one-sided "okres do .."
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
    /// Never throws: a description that *looks* like one of the known patterns but names an
    /// impossible date (e.g. "31 lutego") is treated the same as no match at all, not a crash.
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
            && TryBuildDate(endYear, endMonth, endDay, out var end))
        {
            return (new DateOnly(end.Year, end.Month, 1), end);
        }

        var endDateMonthNameMatch = EndDateMonthNameRegex.Match(description);
        if (endDateMonthNameMatch.Success
            && int.TryParse(endDateMonthNameMatch.Groups[1].Value, out var endDay2)
            && int.TryParse(endDateMonthNameMatch.Groups[3].Value, out var endYear2)
            && PolishMonthForms.TryGetValue(endDateMonthNameMatch.Groups[2].Value, out var endMonth2)
            && TryBuildDate(endYear2, endMonth2, endDay2, out var end2))
        {
            return (new DateOnly(end2.Year, end2.Month, 1), end2);
        }

        var monthYearMatch = MonthYearRegex.Match(description);
        if (monthYearMatch.Success
            && int.TryParse(monthYearMatch.Groups[1].Value, out var month)
            && int.TryParse(monthYearMatch.Groups[2].Value, out var year)
            && TryBuildDate(year, month, 1, out var first))
        {
            return (first, first.AddMonths(1).AddDays(-1));
        }

        var monthNameMatch = MonthNameYearRegex.Match(description);
        if (monthNameMatch.Success
            && int.TryParse(monthNameMatch.Groups[2].Value, out var namedYear)
            && PolishMonthForms.TryGetValue(monthNameMatch.Groups[1].Value, out var namedMonth)
            && TryBuildDate(namedYear, namedMonth, 1, out var namedFirst))
        {
            return (namedFirst, namedFirst.AddMonths(1).AddDays(-1));
        }

        return null;
    }

    private static bool TryBuildDate(int year, int month, int day, out DateOnly result)
    {
        if (month is >= 1 and <= 12 && year is >= 1 and <= 9999 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
        {
            result = new DateOnly(year, month, day);
            return true;
        }
        result = default;
        return false;
    }

    private static bool TryParsePolishDate(string text, out DateOnly result)
    {
        string[] formats = ["dd.MM.yyyy", "dd-MM-yyyy", "dd/MM/yyyy", "d.M.yyyy", "dd.MM.yy", "d.MM.yyyy", "dd.M.yyyy"];
        foreach (var format in formats)
        {
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                return true;
        }
        result = default;
        return false;
    }

    /// <summary>
    /// For a product line with no real catalog symbol (see SferaInvoiceReader - nexo sometimes
    /// has none), derives a grouping key from its description with the period-varying parts
    /// stripped out, so that e.g. "Za okres do 14-styczeń 2026" and "Za okres do 14-luty 2026"
    /// normalize to the same key (correctly: same recurring line, different month) while an
    /// unrelated line on the same invoice *position* does not collide with it just because
    /// neither has a catalog symbol. Exposed from Core so the Server-side reader - the only
    /// place that needs to invent a symbol at all - can call it; this is pure text normalization,
    /// nothing nexo-specific about the logic itself.
    /// </summary>
    public static string NormalizeDescriptionForGrouping(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return string.Empty;

        string normalized = RangeRegex.Replace(description, "okres");
        normalized = EndDateNumericRegex.Replace(normalized, "okres");
        normalized = EndDateMonthNameRegex.Replace(normalized, "okres");
        normalized = MonthYearRegex.Replace(normalized, "za");
        normalized = MonthNameYearRegex.Replace(normalized, "za");
        normalized = Regex.Replace(normalized, @"\d+", "#");
        return normalized.Trim().ToLowerInvariant();
    }
}
