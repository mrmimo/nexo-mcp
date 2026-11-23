namespace NexoMcp.Core.Models;

public enum DocumentKind
{
    Invoice,

    /// <summary>
    /// Not currently produced by SferaInvoiceReader: a nexo proforma invoice is a different CLR
    /// type (DokumentZK, via ZamowieniaOdKlientow) from the sales/purchase invoices this project
    /// reads (DokumentDS/DokumentDZ), and nothing maps DokumentZK yet. The "exclude Proforma"
    /// filters in RevenueAnalyzer/ExpenseAnalyzer are therefore a no-op today, not a guarantee -
    /// kept for when/if proforma reading is added, rather than removed and re-added later.
    /// </summary>
    Proforma,

    Correction,
}
