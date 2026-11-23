using InsERT.Moria.Sfera;

namespace NexoMcp.Server.Sfera;

/// <summary>
/// Opens a logged-in Sfera handle, following the exact connection pattern already proven
/// elsewhere in this project (DanePolaczenia.Jawne -> MenedzerPolaczen.Polacz ->
/// ZalogujOperatora). Read-only use: callers must never call anything under a document's
/// Zapisz()/write surface through a handle obtained here.
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
            uzytkownikSerwera: config.SqlLogin,
            hasloUzytkownikaSerwera: config.SqlPassword,
            szyfrowaniePolaczeniaZSerwerem: false,
            nazwaUzytkownikaDoImpersonacjiWBazie: null,
            katalogBinariow: AppContext.BaseDirectory,
            katalogPlikowKonfiguracyjnych: null,
            katalogTymczasowy: null,
            plikStartowy: null,
            nazwaWdrozenia: null);

        var menedzerPolaczen = new MenedzerPolaczen();
        Uchwyt sfera = menedzerPolaczen.Polacz(danePolaczenia, config.ProductId, (IPostepLadowaniaSfery?)null);

        try
        {
            // NEXO_WINDOWS_AUTH only controls the SQL Server connection layer above
            // (autentykacjaWindowsDoSerwera). Logging into Sfera as a nexo *operator* is a
            // separate, always-required layer on top of that - every proven call site in this
            // project calls ZalogujOperatora unconditionally. An earlier version of this file
            // wrongly skipped it when NEXO_WINDOWS_AUTH=true - fixed here.
            //
            // ZalogujOperatora's return value prints as "True"/"False" on a real connection, but
            // its actual CLR type was deliberately left unchecked here - comparing it against a
            // guessed enum member name risked not compiling against a different SDK version. A
            // failed login still surfaces as a clear error - just from whatever Sfera call
            // happens next, not from this line.
            var wynikLogowania = sfera.ZalogujOperatora(config.Login, config.Password ?? string.Empty);
            Console.Error.WriteLine($"Wynik logowania operatora nexo: {wynikLogowania}");
            return sfera;
        }
        catch
        {
            // ZalogujOperatora throwing (bad credentials, nexo unreachable mid-handshake, ...)
            // used to leak the connection this method already opened - nothing downstream of
            // this method would ever see "sfera" to dispose it.
            sfera.Dispose();
            throw;
        }
    }
}
