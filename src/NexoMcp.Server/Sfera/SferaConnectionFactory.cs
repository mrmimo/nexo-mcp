using InsERT.Moria.Sfera;
using InsERT.Mox.Product;

namespace NexoMcp.Server.Sfera;

/// <summary>
/// Opens a logged-in Sfera handle, following the exact connection pattern already proven in this
/// company's own integrator (DanePolaczenia.Jawne -> MenedzerPolaczen.Polacz -> ZalogujOperatora).
/// Read-only use: callers must never call anything under a document's Zapisz()/write surface
/// through a handle obtained here.
/// </summary>
public static class SferaConnectionFactory
{
    public static Uchwyt Connect(NexoConfig config)
    {
        // katalogBinariow must be passed explicitly: it tells Sfera's launcher where to find the
        // rest of the nexo SDK's own assemblies (e.g. InsERT.Mox.Launcher.dll), which it loads
        // dynamically by name rather than through a normal .NET reference. Left at its default,
        // it falls back to the process's current working directory, not this executable's own
        // folder - and an MCP client is not guaranteed to launch us from that folder. We ship all
        // of those assemblies next to our own build output (see NexoMcp.Server.csproj's
        // CopyNexoSdkRuntimeFiles target), so AppContext.BaseDirectory is always right.
        var danePolaczenia = DanePolaczenia.Jawne(
            serwer: config.Server,
            baza: config.Database,
            autentykacjaWindowsDoSerwera: config.WindowsAuthentication,
            uzytkownikSerwera: null,
            hasloUzytkownikaSerwera: null,
            szyfrowaniePolaczeniaZSerwerem: false,
            nazwaUzytkownikaDoImpersonacjiWBazie: null,
            katalogBinariow: AppContext.BaseDirectory,
            katalogPlikowKonfiguracyjnych: null,
            katalogTymczasowy: null,
            plikStartowy: null,
            nazwaWdrozenia: null);

        var productId = Enum.Parse<ProductId>(config.ProductId, ignoreCase: true);

        var menedzerPolaczen = new MenedzerPolaczen();
        Uchwyt sfera = menedzerPolaczen.Polacz(danePolaczenia, productId, (IPostepLadowaniaSfery?)null);

        // NEXO_WINDOWS_AUTH only controls the SQL Server connection layer above
        // (autentykacjaWindowsDoSerwera). Logging into Sfera as a nexo *operator* is a separate,
        // always-required layer on top of that - every proven call site in this codebase
        // (Eksperyment_ProformaIFaktura, LokalnyIntegratorNexo) calls ZalogujOperatora
        // unconditionally. An earlier version of this file wrongly skipped it when
        // NEXO_WINDOWS_AUTH=true - fixed here.
        if (string.IsNullOrWhiteSpace(config.Login))
        {
            sfera.Dispose();
            throw new InvalidOperationException("NEXO_LOGIN jest wymagany (login operatora nexo, niezaleznie od NEXO_WINDOWS_AUTH).");
        }

        // ZalogujOperatora's return type/success value isn't pinned down against a live
        // instance yet (see README "Status") - we only log it, the way the existing
        // proof-of-concept code in this company's integrator does, rather than guessing at an
        // enum member name that might not compile against your SDK version.
        var wynikLogowania = sfera.ZalogujOperatora(config.Login, config.Password ?? string.Empty);
        Console.Error.WriteLine($"Wynik logowania operatora nexo: {wynikLogowania}");

        return sfera;
    }
}
