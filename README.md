# nexo-mcp

An [MCP](https://modelcontextprotocol.io) server that lets an AI assistant query invoices issued
in **InsERT Subiekt / Gestor nexo** - with their line items, products, clients and dates - and
run simple analytics on top: finding invoices that are billed on a recurring schedule, the
periods they cover, average monthly revenue, and a next-year budget projection.

Read-only. It never creates, edits, or deletes anything in nexo.

## Why this exists

InsERT nexo doesn't expose its data over a network API by default - the supported way to read
or write its data from outside the application is **Sfera**, InsERT's own .NET object model
(shipped with the nexo SDK, requires your own licensed nexo installation). This project wraps
Sfera in an MCP server so tools like Claude can answer questions such as:

- "List the invoices for client X in 2025, with line items."
- "Which clients are billed on a recurring (monthly/quarterly/yearly) basis, and for what?"
- "What's our average monthly revenue over the last 12 months?"
- "Draft a revenue budget for next year based on this year's invoices."

### Why Sfera and not raw SQL against the nexo database

Direct SQL is tempting (and InsERT does support it for custom reports), but nexo's physical
database schema **changes between versions** (see your SDK's `ZmianyModeluDanych_*.txt` files).
Since this is meant to be used by different people on different nexo versions, hardcoded table
names would quietly break for someone else. Sfera is the version-stable contract, at the cost of
being slower than raw SQL for bulk reads - which is why this server keeps a local cache (see
below) instead of re-querying Sfera on every question.

## How it works

```
nexo (Sfera) --sync--> local SQLite cache --analytics--> MCP tools --stdio--> your AI assistant
```

- **NexoMcp.Core** - plain C#, no nexo dependency: the `Invoice`/`InvoiceLine` model, the SQLite
  cache, and the analytics (recurring-invoice detection, monthly revenue, budget projection).
  Fully unit tested (`dotnet test`).
- **NexoMcp.Server** - the MCP host. On startup (and then on a timer), it logs into nexo through
  Sfera, reads invoices, and upserts them into the local cache; the actual MCP tools only ever
  read from that cache, so answering a question doesn't open a new Sfera session each time.

## Status

Validated against a real nexo 62.0.2 installation (read-only, against a test company database):
connects, logs in as an operator, and syncs invoices with correct amounts (net/gross/VAT
cross-checked against real figures) and line items into the cache. A couple of fields are marked
`TODO`/"best effort" in `src/NexoMcp.Server/Sfera/SferaInvoiceReader.cs`'s comments where the SDK
docs alone weren't conclusive (notably: the invoice due date isn't populated yet, and currency
is hardcoded to PLN - see "Known limitations"). If something looks wrong against your
installation or a different SDK version, that file is the only place nexo-specific field names
appear - issues and PRs very welcome.

Two non-obvious setup gotchas this project's own build already works around (see
`NexoMcp.Server.csproj` and `Sfera/NexoSdkAssemblyResolver.cs` if you're porting this pattern
elsewhere): Sfera loads part of the rest of the nexo SDK dynamically by assembly name at
runtime, which .NET 8 (unlike classic .NET Framework) won't resolve just because the DLL sits
next to the exe; and `DanePolaczenia.Jawne`'s `katalogBinariow` must be passed explicitly as
`AppContext.BaseDirectory`, or it resolves relative to the process's current working directory
instead of the executable's own folder.

## Setup

**Prerequisites:** Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download), and your own
licensed InsERT nexo installation (Subiekt or Gestor).

### 1. Point the build at your nexo SDK

The Sfera assemblies (`InsERT.Moria.*.dll`) are proprietary and **not included** in this repo.
Either:

- Copy them from your nexo SDK's `Bin` folder into a local `sdk/` folder at the repo root
  (gitignored), or
- Set the `NEXO_SDK_PATH` environment variable to that `Bin` folder directly.

Required files: `InsERT.Moria.API.dll`, `InsERT.Moria.DaneDomyslne.dll`,
`InsERT.Moria.ModelDanych.dll`, `InsERT.Moria.Sfera.dll`, `InsERT.Mox.Core.dll`,
`InsERT.Mox.EntityFramework.Core.dll`, `InsERT.Mox.EntityFrameworkSupport.dll`, plus the runtime
files `Xml.pak`, `sni.dll`, `ijwhost.dll`, `Microsoft.Data.SqlClient.SNI.dll`.

### 2. Configure the connection

```powershell
cp .env.example .env
```

Fill in `NEXO_SERVER` / `NEXO_DATABASE` (and `NEXO_LOGIN` / `NEXO_PASSWORD` if you're not using
Windows authentication). See `.env.example` for every variable.

### 3. Build and test

```powershell
dotnet build NexoMcp.sln
dotnet test tests/NexoMcp.Core.Tests/NexoMcp.Core.Tests.csproj
```

### 4. Point your MCP client at it

Example for an MCP client's JSON config (adjust the path to your build output):

```json
{
  "mcpServers": {
    "nexo": {
      "command": "C:/path/to/nexo-mcp/src/NexoMcp.Server/bin/Debug/net8.0-windows10.0.17763.0/win-x64/NexoMcp.Server.exe",
      "env": {
        "NEXO_SERVER": "(local)\\INSERTNEXO",
        "NEXO_DATABASE": "YourCompanyDb"
      }
    }
  }
}
```

(Or load the same variables from your `.env` file, depending on what your client supports.)

## Tools

| Tool | Description |
|---|---|
| `list_invoices` | List cached invoices (headers only), filterable by date range / client. |
| `get_invoice` | Full detail of one invoice, including every line item. |
| `find_recurring_invoices` | Client+product pairs billed on a regular cadence, with their detected period where parseable from the line description. |
| `get_monthly_revenue` | Calendar-month revenue breakdown and average. |
| `project_budget` | Simple next-year monthly budget (flat trailing average, or seasonal per-month average), with an optional growth rate. |
| `sync_invoices` | Force a re-sync from nexo right now (otherwise runs automatically). |
| `get_sync_status` | When the cache was last synced. |

## Known limitations

- No dedicated "billing period" field exists anywhere in nexo's data model - `find_recurring_invoices`
  parses it out of the line's free-text description (a handful of common Polish phrasings); a
  `null` period means it couldn't be parsed, not that there wasn't one.
- Invoice due date isn't populated yet (see "Status" above).
- Currency is hardcoded to PLN.
- `clientSymbol` is currently nexo's internal numeric client id, not a human-readable customer
  code - the dedicated code/symbol field on nexo's client entity couldn't be confirmed from the
  SDK docs alone. `clientName` is read normally; tax id (NIP) isn't surfaced yet.
- `project_budget` is a simple historical average with an optional manual growth rate, not a
  forecasting model - a first draft, not a substitute for judgement.

## Contributing

Issues and PRs welcome, especially test reports against a real nexo instance and corrections to
`SferaInvoiceReader.cs`.

## About

This project started out of work automating recurring billing on top of InsERT nexo, done by
[Proinet](https://proinet.pl/). If you're interested in similar automation for your own nexo
setup, feel free to get in touch via [proinet.pl](https://proinet.pl/).

## License

[MIT](LICENSE)
