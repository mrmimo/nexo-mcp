# nexo-mcp

Serwer [MCP](https://modelcontextprotocol.io), który pozwala asystentowi AI odpytywać
**InsERT Subiekt / Gestor nexo**: faktury sprzedaży i zakupu (koszty) wraz z pozycjami,
produktami i klientami, a także operacje bankowe/kasowe (dzięki czemu widać też płatności ZUS
i do Urzędu Skarbowego - które nigdy nie mają swojej faktury) - i liczyć na tym proste analizy:
wykrywanie faktur rozliczanych cyklicznie oraz okresów, które pokrywają, miesięczne przychody,
miesięczne koszty i projekcję budżetu na kolejny rok, liczoną jako różnica przychodów i kosztów.

Tylko odczyt. Nigdy nie tworzy, nie edytuje i nie usuwa niczego w nexo.

## Dlaczego to powstało

InsERT nexo domyślnie nie wystawia swoich danych przez sieciowe API - wspierany sposób
odczytu/zapisu danych z zewnątrz to **Sfera**, własny model obiektowy .NET firmy InsERT
(dostarczany z SDK nexo, wymaga własnej licencjonowanej instalacji nexo). Ten projekt owija
Sferę w serwer MCP, żeby narzędzia takie jak Claude mogły odpowiadać na pytania w stylu:

- "Pokaż faktury dla klienta X w 2025, z pozycjami."
- "Którzy klienci są rozliczani cyklicznie (miesięcznie/kwartalnie/rocznie) i za co?"
- "Jaki mamy średni miesięczny przychód z ostatnich 12 miesięcy? A koszty?"
- "Zaproponuj budżet na kolejny rok na podstawie przychodów i kosztów tego roku, z ZUS i podatkiem."

### Dlaczego Sfera, a nie surowe SQL do bazy nexo

Bezpośrednie SQL jest kuszące (InsERT wspiera to nawet dla własnych raportów), ale fizyczny
schemat bazy nexo **zmienia się między wersjami** (zobacz pliki `ZmianyModeluDanych_*.txt` w
Twoim SDK). Ponieważ ten projekt ma być używany przez różne osoby na różnych wersjach nexo,
zahardkodowane nazwy tabel po cichu przestałyby działać komuś innemu. Sfera jest kontraktem
stabilnym między wersjami, kosztem wolniejszego odczytu niż surowe SQL - stąd lokalny cache
(patrz niżej) zamiast odpytywania Sfery przy każdym pytaniu.

## Jak to działa

```
nexo (Sfera) --sync--> lokalny cache SQLite --analizy--> narzędzia MCP --stdio--> Twój asystent AI
```

- **NexoMcp.Core** - czysty C#, bez zależności od nexo: model (`Invoice`/`InvoiceLine`/
  `CashOperation`), cache SQLite i analizy (wykrywanie cykliczności, miesięczne przychody,
  miesięczne koszty, projekcja budżetu). Przetestowane jednostkowo (`dotnet test`) - głównie logika
  analiz i cache; kategoryzacja operacji bankowych (ZUS/podatek/VAT, w `NexoMcp.Server`) nie ma
  jeszcze własnych testów, bo wymaga typów z SDK nexo - patrz "Znane ograniczenia".
- **NexoMcp.Server** - host MCP. Przy starcie (i potem okresowo, w tle - nie blokując startu
  serwera) loguje się do nexo przez Sferę, odczytuje faktury sprzedaży, zakupu i operacje
  bankowo-kasowe, i zapisuje je do lokalnego cache; same narzędzia MCP czytają tylko z tego
  cache, więc odpowiedź na pytanie nie otwiera nowej sesji Sfery.

Cache jest jednorazowy/odtwarzalny - buduje się od nowa z nexo, to nie jest system źródłowy.
Aktualizacja do wersji z innym schematem cache przebudowuje go automatycznie (patrz "Status") -
nie trzeba już ręcznie usuwać pliku. To nie jest jednak migracja *danych*: po przebudowie
historia sprzed aktualizacji po prostu wraca z nexo przy najbliższej pełnej synchronizacji
(ostatnie 3 lata, na stałe w kodzie, raz na 24h - zobacz `SyncBackgroundService.cs`), a nie jest
"migrowana" w żadnym innym sensie.

## Status

Zweryfikowane na żywo na prawdziwej instalacji nexo 62.0.2 (odczyt, najpierw na testowej, potem
na produkcyjnej bazie firmowej): łączy się, loguje jako operator, i synchronizuje faktury
sprzedaży, zakupu oraz operacje bankowo-kasowe z poprawnymi kwotami (netto/brutto/VAT
porównane z realnymi liczbami) - w tym prawdziwych klientów rozliczanych cyklicznie i
prawdziwe płatności ZUS/US, poprawnie wyłowione z kilkuset operacji bankowych.

Projekt przeszedł kilka niezależnych rund przeglądu kodu (model Opus, bez pamięci między rundami)
pod kątem poprawności i jakości, każda sprawdzająca poprawki poprzedniej od nowa (nie na słowo) i
szukająca nowych problemów - część widoczna tylko po uruchomieniu na żywych danych, część tylko
przy analizie kodu. Ostatnia runda uznała projekt za gotowy, z drobnymi uwagami tekstowymi (już
uwzględnionymi poniżej/w "Znane ograniczenia"). Co zostało naprawione w trakcie tych rund:

- **Koszty liczyły się podwójnie, a VAT wliczał się do kosztów.** Operacja bankowa rozliczająca
  fakturę zakupu (już policzoną jako koszt) też wpadała do kosztów jako "Other", a VAT
  odprowadzany do US liczył się jako koszt, mimo że przychód jest liczony netto z VAT. Naprawione
  przez wykluczenie operacji rozliczających dokument, transferów wewnętrznych oraz transferów VAT -
  rozpoznawanych trzema sygnałami: polem `TypTransferuVAT` dla przelewów z rachunku VAT, wzorcem
  "/VAT/" w tytule dla przelewów w mechanizmie podzielonej płatności (potwierdzone na żywych
  danych - to przelew do *dostawcy*, nie do urzędu skarbowego, ale rozlicza fakturę już policzoną
  jako koszt), i wzorcem "/SFP/VAT.../JPK_V7..." dla zwykłego (nie split-payment) przelewu
  deklaracji VAT do urzędu skarbowego z głównego rachunku - ten trzeci wzorzec nie został
  potwierdzony na żywym przykładzie (testowana firma rozlicza VAT wyłącznie przez podzieloną
  płatność), tylko wyprowadzony z potwierdzonego na żywo kształtu tytułu dla PIT/CIT
  ("/TI/.../OKR/.../SFP/PIT4/TXT/...") - zobacz `SferaCashFlowReader.cs`. Rozliczenie przez
  `Rozrachunek` bez bezpośredniego `PlatnoscDokumentu` nie jest jeszcze sprawdzane - patrz "Znane
  ograniczenia".
- **Synchronizacja przyrostowa trwale gubiła dane wprowadzone z opóźnieniem.** Każda
  synchronizacja odczytywała tylko okno od ostatniej synchronizacji minus 7 dni - faktura zakupu
  wprowadzona do nexo z opóźnieniem większym niż tydzień (typowe przy księgowości robionej raz w
  miesiącu) nigdy nie trafiała do cache, i usunięty dokument starszy niż ten margines zostawał w
  cache na zawsze. Naprawione: oprócz synchronizacji przyrostowej co godzinę, raz na 24h serwer
  robi pełną synchronizację całego zachowywanego okna (3 lata, na stałe w kodzie) - zobacz
  `SyncBackgroundService.cs`.
- **Średnie i budżet pomijały miesiące bez danych i liczyły niedokończony miesiąc.** Średnia z
  "ostatnich 12 miesięcy" liczyła się tylko z miesięcy, w których były jakieś faktury - dwa
  stycznie rok po roku, bez niczego między nimi, liczyły się jak dwa kolejne miesiące. Naprawione:
  `RevenueAnalyzer`/`ExpenseAnalyzer` budują teraz gęsty szereg miesięczny (z zerami w brakujących
  miesiącach) i nigdy nie liczą bieżącego, niedokończonego miesiąca.
- **Serwer nie startował wg instrukcji z README, i start faktycznie blokował się na synchronizacji.**
  `NEXO_LOGIN` był w kodzie zawsze wymagany, ale przykład konfiguracji w README go nie podawał;
  `NEXO_WINDOWS_AUTH=false` nie miał w ogóle zaimplementowanego logowania do SQL Servera; literówka
  w `.csproj` psuła budowanie, gdy `NEXO_SDK_PATH` nie miał końcowego backslasha (dokładnie tak,
  jak podawał przykład w README); pierwsza wersja przeniesienia synchronizacji do tła nadal
  blokowała start hosta, bo nie oddawała sterowania przed pierwszym `await`. Wszystko naprawione -
  zobacz "Konfiguracja" niżej.
- **Jedno współdzielone połączenie SQLite blokowało zapytania narzędzi w trakcie synchronizacji.**
  `NexoCache` otwiera teraz osobne połączenie na każdą operację (z WAL dla pliku na dysku), a
  `SyncService` ma semafor, który nie dopuszcza dwóch równoległych synchronizacji.
- **Kopiowanie plików SDK nadpisywało poprawne wersje zależności NuGet.** Projekt kopiuje całą
  zawartość folderu `Bin` z SDK nexo (bo Sfera dociąga część z niego dynamicznie) - ale robił to
  nawet dla plików, które NuGet już poprawnie umieścił w katalogu wynikowym (np. inna, niezgodna
  binarnie wersja `Microsoft.Data.Sqlite`/`SQLitePCLRaw` niż ta, z którą projekt jest budowany i
  testowany). Naprawione: kopiowanie pomija pliki już dostarczone przez NuGet.
- Brak wersjonowania schematu cache powodował, że aktualizacja do nowszej wersji z innym
  schematem kończyła się błędem SQL ("no such column") na każdym wywołaniu. Naprawione: cache
  sprawdza teraz `PRAGMA user_version` i sam się przebudowuje przy niezgodności, zamiast wymagać
  ręcznego usunięcia pliku.
- Wykrywanie cykliczności rzucało wyjątkiem na niemożliwej dacie w opisie pozycji (np. "31
  lutego") i nie rozpoznawało większości polskich nazw miesięcy w dopełnieniu ("stycznia",
  "lutego"...) - rozpoznawało tylko mianownik. Pozycje bez symbolu katalogowego na tej samej
  pozycji na fakturze (np. zawsze pierwsza linia) błędnie łączyły się w jedną serię, nawet gdy to
  były różne produkty. Wszystko naprawione.
- Walutę brano zawsze jako PLN, mimo że Sfera ją udostępnia (`Dokument.Waluta.Symbol`) - teraz
  jest mapowana poprawnie (kwoty w walutach innych niż PLN nie są jeszcze przeliczane - patrz
  "Znane ograniczenia").
- **Dokumenty unieważnione/przejściowe liczyły się jak zwykłe, prawidłowe dokumenty.** Naprawione:
  dokumenty w stanie `StatusDokumentu.Uniewazniony`/`Przejsciowy` są teraz pomijane tak samo jak te
  w koszu (`SferaInvoiceReader.cs`) - choć, w odróżnieniu od filtra na kosz, nie zostało to osobno
  zweryfikowane na żywym dokumencie w tym konkretnym stanie - patrz "Znane ograniczenia".

Ten plik i opisy narzędzi MCP są świadomie po polsku - to oprogramowanie dla polskiego rynku
(nexo to polski system), nawet jeśli komentarze w samym kodzie źródłowym są po angielsku (typowe
dla kodu open source, który może czytać/rozwijać ktoś spoza Polski).

Jeśli coś wygląda źle na Twojej instalacji albo innej wersji SDK, nazwy pól specyficzne dla nexo
żyją wyłącznie w folderze `src/NexoMcp.Server/Sfera/` - to tam trzeba szukać, i tam mile widziany
PR z poprawką.

## Konfiguracja

**Wymagania:** Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download) i własna,
licencjonowana instalacja InsERT nexo (Subiekt albo Gestor).

### 1. Wskaż SDK nexo dla budowania projektu

Biblioteki Sfery (`InsERT.Moria.*.dll`) są własnością InsERT i **nie są częścią tego repo** (nie
mamy licencji na ich redystrybucję). Albo:

- Skopiuj **cały** folder `Bin` ze swojego SDK nexo do lokalnego folderu `sdk/` w katalogu
  głównym repo (jest w `.gitignore`), albo
- Ustaw zmienną środowiskową `NEXO_SDK_PATH` wprost na ten folder `Bin`.

Skopiuj **cały folder**, nie tylko kilka plików - Sfera dociąga część SDK dynamicznie w trakcie
działania (np. `InsERT.Mox.Launcher.dll`), a projekt i tak kopiuje wszystkie `*.dll`/`*.pak` z
tego folderu do katalogu wynikowego.

### 2. Skonfiguruj połączenie

```powershell
cp .env.example .env
```

Wypełnij `NEXO_SERVER` / `NEXO_DATABASE` / `NEXO_LOGIN` (login operatora nexo - wymagany
zawsze, niezależnie od sposobu łączenia z SQL Serverem) i ewentualnie `NEXO_SQL_LOGIN` /
`NEXO_SQL_PASSWORD`, jeśli `NEXO_WINDOWS_AUTH=false`. Pełna lista zmiennych jest w
`.env.example`.

### 3. Zbuduj i przetestuj

```powershell
dotnet build NexoMcp.sln
dotnet test tests/NexoMcp.Core.Tests/NexoMcp.Core.Tests.csproj
```

### 4. Podłącz swojego klienta MCP

Przykład konfiguracji JSON klienta MCP (dopasuj ścieżkę do swojego katalogu wynikowego):

```json
{
  "mcpServers": {
    "nexo": {
      "command": "C:/sciezka/do/nexo-mcp/src/NexoMcp.Server/bin/Debug/net8.0-windows10.0.17763.0/win-x64/NexoMcp.Server.exe",
      "env": {
        "NEXO_SERVER": "(local)\\INSERTNEXO",
        "NEXO_DATABASE": "TwojaBazaFirmowa",
        "NEXO_LOGIN": "TwojLoginOperatora",
        "NEXO_PASSWORD": "TwojeHaslo"
      }
    }
  }
}
```

(Albo wczytaj te same zmienne z pliku `.env`, zależnie od tego, co wspiera Twój klient.)

## Narzędzia

| Narzędzie | Opis |
|---|---|
| `list_invoices` | Lista faktur z cache (tylko nagłówki), filtrowana po zakresie dat, kliencie/dostawcy i kierunku (sprzedaż/zakup). |
| `get_invoice` | Pełny szczegół jednej faktury (sprzedaży lub zakupu), z każdą pozycją. |
| `find_recurring_invoices` | Każda para klient+produkt rozliczana co najmniej 3 razy, oznaczona jako `Monthly`/`Quarterly`/`Yearly`/`Irregular` według rzeczywistej regularności odstępów (nic nie jest ukrywane za niedoskonałą regularność - z pewną tolerancją na okazjonalny skok/przesunięcie, patrz "Znane ograniczenia"), z wykrytym okresem, jeśli da się go odczytać z opisu pozycji. |
| `get_monthly_revenue` | Rozkład przychodów sprzedaży po miesiącach i średnia. |
| `get_monthly_expenses` | Rozkład kosztów po miesiącach: faktury zakupu, ZUS, podatek, inne operacje bankowo-kasowe. |
| `project_budget` | Budżet na kolejny rok dla przychodów, kosztów i ich różnicy netto (płaska średnia z ostatnich 12 miesięcy albo sezonowa średnia per miesiąc), z niezależną stopą wzrostu dla przychodów i kosztów. |
| `sync_invoices` | Wymusza natychmiastową synchronizację z nexo (inaczej dzieje się to automatycznie). |
| `get_sync_status` | Kiedy cache był ostatnio synchronizowany. |

## Znane ograniczenia

- W modelu danych nexo nie istnieje żadne dedykowane pole "okres rozliczeniowy" -
  `find_recurring_invoices` wyciąga je z wolnego tekstu opisu pozycji (kilka popularnych polskich
  zwrotów); `null` jako okres znaczy "nie udało się rozpoznać", nie "nie było okresu".
- Klasyfikacja Monthly/Quarterly/Yearly w `find_recurring_invoices` toleruje *pewną* nieregularność
  odstępów (np. jeden pominięty/przesunięty miesiąc u klienta rozliczanego miesięcznie), ale to nie
  jest gwarancja - kilka nieregularności naraz, albo jedna duża, i para trafi do `Irregular`, mimo
  że w praktyce jest rozliczana cyklicznie. `Irregular` to uczciwa etykieta w takim przypadku, a
  nie błąd.
- `clientSymbol` korzysta z `Sygnatura.PelnaSygnatura` klienta, jeśli jest ustawiona, a w
  przeciwnym razie z wewnętrznego numerycznego id - jeśli Twoja kartoteka klientów nie ma
  wypełnionych sygnatur, zobaczysz numeryczne id.
- Kategoryzacja ZUS/podatek dla operacji bankowych i kasowych to dopasowywanie słów kluczowych w
  nazwie kontrahenta/tytule ("ZUS", "Skarbow[y/a]", "podat...") z granicą słowa (nie dopasuje się
  w środku innego słowa), a nie strukturalne pole nexo - zobacz "Status". Płatność ZUS/podatku pod
  nierozpoznaną nazwą kontrahenta trafi do "Other". Ta logika (w `SferaCashFlowReader.cs`) nie ma
  jeszcze własnych testów jednostkowych, bo wymaga typów z SDK nexo, których testy w `NexoMcp.Core`
  nie mają dostępu.
- Koszt z faktury zakupu liczy się w miesiącu jej *wystawienia*, nie faktycznej zapłaty;
  `get_monthly_expenses`/`project_budget` po stronie kosztów miesza tę liczbę memoriałową z
  kasowymi operacjami bankowo-kasowymi (ZUS, podatek itd.) - wystarczające na pierwszy szkic
  budżetu, ale to nie jest ścisły rachunek przepływów pieniężnych (cash flow).
- Kwoty w walutach innych niż PLN nie są przeliczane - jeśli wystawiasz faktury w EUR albo masz
  konto walutowe, te kwoty wejdą do sum tak, jakby były w PLN. Waluta dokumentu jest poprawnie
  odczytywana (`currency` w `list_invoices`/`get_invoice`), ale sumy w analizach jej nie
  uwzględniają - traktuj zestawienia jako nieprecyzyjne, jeśli masz dokumenty w innych walutach.
- `project_budget` to prosta średnia historyczna z opcjonalną ręczną stopą wzrostu, nie model
  prognostyczny - pierwszy szkic, nie zamiennik własnej oceny.
- Przychody/koszty sumują się po wszystkich dokumentach `DokumentDS`/`DokumentDZ` bez filtrowania
  po ich typie (np. zaliczkowe vs końcowe) - na danych testowych wszystkie dokumenty były zwykłymi
  fakturami (`FS`/`FZ`), bez faktur zaliczkowych, więc ten konkretny scenariusz nie został
  sprawdzony na żywo. Jeśli wystawiasz/otrzymujesz faktury zaliczkowe rozliczane potem fakturą
  końcową za to samo zamówienie, sprawdź w `list_invoices`, czy zaliczkowa i końcowa nie wchodzą
  obie do `get_monthly_revenue`/`get_monthly_expenses` jako odrębny przychód/koszt.
- **Rozliczenie faktury zakupu przez `Rozrachunek`, bez bezpośredniego `PlatnoscDokumentu`, może
  się liczyć podwójnie** - raz jako koszt faktury (memoriałowo, w miesiącu wystawienia) i drugi
  raz jako operacja bankowa/kasowa w kategorii "Other" (bo nie ma sygnału, który łączyłby tę
  operację z konkretną fakturą - zobacz `SferaCashFlowReader.cs`). To nie jest sprawdzane dziś,
  nie dlatego że jest niemożliwe do sprawdzenia, ale dlatego że nie zostało jeszcze zrobione:
  `Rozrachunek`/`PrzeplywFinansowy` mają strukturalne odwołania (`Dokument`, `PrzeplywFinansowy`),
  które prawdopodobnie dałoby się wykorzystać do dopasowania - `DokumentZrodlowy` (pole
  tekstowe) nie jest jedyną możliwą ścieżką, tylko tą, która została odrzucona jako za mało
  wiarygodna. Jeśli rozliczasz faktury zakupu głównie przez rozrachunki, a nie przez proste
  przelewy z `PlatnoscDokumentu`, koszty z `get_monthly_expenses`/`project_budget` mogą być
  zawyżone.
- Filtrowanie dokumentów unieważnionych/przejściowych (`StatusDokumentu.Uniewazniony`/
  `Przejsciowy` w `SferaInvoiceReader.cs`) opiera się na polach potwierdzonych przez refleksję na
  prawdziwym SDK, ale - w odróżnieniu od `IsInRecycleBin` - nie zostało osobno zweryfikowane na
  żywym dokumencie w tym konkretnym stanie; jeśli Twoja instalacja ma unieważnione/przejściowe
  dokumenty w historii, warto sprawdzić, czy faktycznie wypadają z wyników.
- Korekty faktur sumują się z oryginałem na założeniu, że nexo przechowuje ich kwoty jako różnicę
  (dodatnią/ujemną) względem oryginału, a nie jako nowy stan końcowy - to konkretne założenie
  **nie zostało zweryfikowane na żywej korekcie** (firma, na której to było testowane, nie miała
  żadnej korekty w całej historii dokumentów). Jeśli Twoje korekty wyglądają inaczej, przychody i
  koszty za korygowany miesiąc mogą być błędne - zobacz `RevenueAnalyzer.cs`/`ExpenseAnalyzer.cs`.

## Współpraca

Issues i PR mile widziane, zwłaszcza raporty z testów na prawdziwej instalacji nexo i poprawki do
plików w `src/NexoMcp.Server/Sfera/`.

## O projekcie

Ten projekt powstał przy pracach nad automatyzacją cyklicznego rozliczania na InsERT nexo,
zrobionych przez [Proinet](https://proinet.pl/). Jeśli interesuje Cię podobna automatyzacja dla
własnej instalacji nexo, śmiało napisz przez [proinet.pl](https://proinet.pl/).

## Licencja

[MIT](LICENSE)
