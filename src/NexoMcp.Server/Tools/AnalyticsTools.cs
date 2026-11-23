using System.ComponentModel;
using ModelContextProtocol.Server;
using NexoMcp.Core.Analytics;
using NexoMcp.Core.Cache;
using NexoMcp.Core.Models;

namespace NexoMcp.Server.Tools;

[McpServerToolType]
public sealed class AnalyticsTools(NexoCache cache)
{
    [McpServerTool(Name = "find_recurring_invoices"), Description(
        "Znajduje kazda pare klient+produkt rozliczana co najmniej minOccurrences razy, oznaczona " +
        "wedlug rzeczywistej regularnosci odstepow miedzy datami wystawienia: Monthly, Quarterly, " +
        "Yearly albo Irregular - nic nie jest ukrywane za niedoskonala regularnosc. Pewna " +
        "tolerancja na okazjonalnie pominiety/przesuniety miesiac jest wliczona (klient " +
        "rozliczany miesiecznie z jednym odstajacym odstepem moze nadal wypadac jako Monthly), " +
        "ale to nie jest gwarancja - kilka pominietych/przesunietych miesiecy, albo jeden bardzo " +
        "duzy odstep, i para trafi do Irregular mimo ze \"w zyciu\" jest rozliczana cyklicznie; " +
        "Irregular to uczciwa etykieta w takim przypadku, nie blad. Dla kazdej serii probuje tez " +
        "odczytac okres rozliczeniowy " +
        "(np. \"za okres od .. do ..\") z tekstu opisu pozycji - nexo nie ma dedykowanego pola " +
        "okresu, wiec null jako okres znaczy \"nie udalo sie rozpoznac\", nie \"nie bylo okresu\".")]
    public object FindRecurringInvoices(
        [Description("Minimalna liczba wystapien, zeby para klient+produkt zostala w ogole zaraportowana. Domyslnie 3.")] int minOccurrences = 3)
    {
        var invoices = cache.GetInvoices(direction: Direction.Sale);
        var series = RecurringInvoiceDetector.Detect(invoices, minOccurrences);

        return series.Select(s => new
        {
            s.ClientSymbol,
            s.ClientName,
            s.ProductSymbol,
            s.ProductDescription,
            Periodicity = s.Periodicity.ToString(),
            AverageIntervalDays = Math.Round(s.AverageIntervalDays, 1),
            s.AverageNetValue,
            Occurrences = s.Occurrences.Select(o => new
            {
                o.InvoiceId,
                o.FullNumber,
                IssueDate = o.IssueDate.ToString("yyyy-MM-dd"),
                o.NetValue,
                PeriodFrom = o.ParsedPeriodFrom?.ToString("yyyy-MM-dd"),
                PeriodTo = o.ParsedPeriodTo?.ToString("yyyy-MM-dd"),
            }),
        });
    }

    [McpServerTool(Name = "get_monthly_revenue"), Description(
        "Rozklada faktury z cache na miesieczny przychod netto/brutto i podaje srednia miesieczna " +
        "(opcjonalnie tylko z ostatnich N miesiecy).")]
    public object GetMonthlyRevenue(
        [Description("Jesli podane, liczy srednia tylko z ostatnich N miesiecy zamiast calej historii.")] int? trailingMonths = null)
    {
        var invoices = cache.GetInvoices(direction: Direction.Sale);
        var months = RevenueAnalyzer.MonthlyBreakdown(invoices);

        return new
        {
            Months = months.Select(m => new { m.Year, m.Month, m.Net, m.Gross, m.Count }),
            AverageMonthlyNet = RevenueAnalyzer.AverageMonthlyNet(months, trailingMonths),
            AverageMonthlyGross = RevenueAnalyzer.AverageMonthlyGross(months, trailingMonths),
            TrailingMonthsUsed = trailingMonths,
        };
    }

    [McpServerTool(Name = "get_monthly_expenses"), Description(
        "Rozklada koszty z cache na miesieczne sumy: faktury zakupu, przelewy ZUS, przelewy do " +
        "urzedu skarbowego i inne operacje bankowo-kasowe (wyplaty, platnosci dostawcom bez " +
        "faktury itd.) - zobacz README \"Znane ograniczenia\" po szczegoly rozpoznawania ZUS/podatku.")]
    public object GetMonthlyExpenses(
        [Description("Jesli podane, liczy srednia tylko z ostatnich N miesiecy zamiast calej historii.")] int? trailingMonths = null)
    {
        var purchaseInvoices = cache.GetInvoices(direction: Direction.Purchase);
        var cashOperations = cache.GetCashOperations(moneyIn: false);
        var months = ExpenseAnalyzer.MonthlyBreakdown(purchaseInvoices, cashOperations);
        var asAmounts = ExpenseAnalyzer.ToMonthlyAmounts(months);

        return new
        {
            Months = months.Select(m => new { m.Year, m.Month, m.Purchases, m.Zus, m.Tax, m.OtherCash, m.Total }),
            AverageMonthlyTotal = RevenueAnalyzer.AverageMonthlyNet(asAmounts, trailingMonths),
            TrailingMonthsUsed = trailingMonths,
        };
    }

    [McpServerTool(Name = "project_budget"), Description(
        "Projektuje prosty budzet na kolejny rok z historii faktur i przeplywow z cache: przychod " +
        "(z faktur sprzedazy), koszty (z faktur zakupu plus ZUS/podatek/inne odplywy bankowo-" +
        "kasowe - zobacz get_monthly_expenses) i ich roznice netto. " +
        "\"flat\" powiela srednia z ostatnich 12 miesiecy na kazdy miesiac docelowego roku. " +
        "\"seasonal\" liczy srednia osobno dla kazdego miesiaca kalendarzowego (budzet na grudzien " +
        "pochodzi z historii poprzednich grudniow itd.), z fallbackiem do sredniej ogolnej dla " +
        "miesiaca bez historii. Opcjonalna stopa wzrostu jest stosowana niezaleznie do przychodow " +
        "i kosztow. To prosta projekcja na bazie sredniej, nie model prognostyczny - traktuj jako " +
        "pierwszy szkic budzetu.")]
    public object ProjectBudget(
        [Description("Rok, dla ktorego projektowac budzet, np. 2027.")] int year,
        [Description("\"flat\" albo \"seasonal\". Domyslnie \"seasonal\".")] string method = "seasonal",
        [Description("Procentowy wzrost/spadek do zastosowania do przychodow, np. 5 dla +5%. Domyslnie 0.")] decimal revenueGrowthRatePercent = 0m,
        [Description("Procentowy wzrost/spadek do zastosowania do kosztow. Domyslnie 0.")] decimal costGrowthRatePercent = 0m)
    {
        ProjectionMethod projectionMethod;
        if (method.Equals("flat", StringComparison.OrdinalIgnoreCase))
            projectionMethod = ProjectionMethod.FlatTrailingAverage;
        else if (method.Equals("seasonal", StringComparison.OrdinalIgnoreCase))
            projectionMethod = ProjectionMethod.SeasonalMonthlyAverage;
        else
            throw new ArgumentException($"Nieznana wartosc method: \"{method}\". Oczekiwano \"flat\" albo \"seasonal\".");

        var revenueMonths = RevenueAnalyzer.MonthlyBreakdown(cache.GetInvoices(direction: Direction.Sale));
        var revenueProjection = BudgetProjector.Project(revenueMonths, year, projectionMethod, revenueGrowthRatePercent);

        var expenseMonths = ExpenseAnalyzer.MonthlyBreakdown(
            cache.GetInvoices(direction: Direction.Purchase), cache.GetCashOperations(moneyIn: false));
        var costProjection = BudgetProjector.Project(
            ExpenseAnalyzer.ToMonthlyAmounts(expenseMonths), year, projectionMethod, costGrowthRatePercent);

        var netByMonth = revenueProjection.Months
            .Join(costProjection.Months, r => r.Month, c => c.Month, (r, c) => new
            {
                Month = r.Month,
                Revenue = r.ProjectedNet,
                Cost = c.ProjectedNet,
                Net = r.ProjectedNet - c.ProjectedNet,
            })
            .ToList();

        return new
        {
            revenueProjection.Year,
            Method = revenueProjection.Method.ToString(),
            RevenueGrowthRatePercentApplied = revenueProjection.GrowthRatePercentApplied,
            CostGrowthRatePercentApplied = costProjection.GrowthRatePercentApplied,
            Months = netByMonth,
            AnnualRevenue = revenueProjection.AnnualTotal,
            AnnualCost = costProjection.AnnualTotal,
            AnnualNet = revenueProjection.AnnualTotal - costProjection.AnnualTotal,
            RevenueHistoryMonthsUsed = revenueMonths.Count,
            CostHistoryMonthsUsed = expenseMonths.Count,
        };
    }
}
