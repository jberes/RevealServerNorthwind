# RevealAI.Engine — Integration Guide

A .NET library that turns a **data schema** (or a live SQL table) into **Reveal `.rdash` dashboards**,
using data profiling + heuristics (and optionally an LLM) to pick and configure visualizations.
Reference it and call one service — `DashboardAiService`. **Your app supplies the connection and the
AI key**; the library owns no configuration of its own.

> Drop this file into the consuming project (e.g. rename to `CLAUDE.md`) so an assistant/developer can
> integrate without reading the library source.

---

## 1. What it does

Three steps, each usable on its own:

1. **Analyze** — profile the columns of a schema (or introspect a live SQL table) and **recommend** a
   ranked list of visualizations as editable `VisualizationSpec`s.
2. **Tweak** — edit those specs (chart type, field bindings, aggregation). Pure data, no LLM.
3. **Generate** — **compile** the specs into a valid `.rdash` (or the `Dashboard.json` string).

The LLM is **optional**: with no API key it uses a deterministic heuristic recommender; with a key it
overlays semantic judgment. Compilation is always deterministic.

---

## 2. Add it to your app (project reference — recommended)

The library targets **net8.0**. Its only non-trivial dependencies are **Reveal.Sdk.Dom** and
**Microsoft.Data.SqlClient** — both of which a Reveal-enabled app already has. (The
`Microsoft.Extensions.*` packages it uses are part of the ASP.NET Core shared framework.)

1. Put the `RevealAI.Engine` project in your solution — copy the `src/RevealAI.Engine` folder in, or add
   it as a **git submodule**.
2. Add a project reference from your server project:
   ```xml
   <ProjectReference Include="..\RevealAI.Engine\RevealAI.Engine.csproj" />
   ```

**Deploying to Azure: nothing special to do.** With a project reference, `dotnet publish` compiles
`RevealAI.Engine` and bundles `RevealAI.Engine.dll` (and its deps, which your Reveal app already pulls)
into your publish output. No private NuGet feed, no token, no extra CI step — your existing
build/deploy pipeline just works. The only build-time requirement is nuget.org access to restore
`Reveal.Sdk.Dom` / `Microsoft.Data.SqlClient`, which your app already needs for Reveal.

> Your host must use **Reveal.Sdk.Dom >= 0.1.668-beta** (the floor the library was built against).
> *(Alternative distribution: the project can also be packed to NuGet — see `nuget.config.sample` and
> `.github/workflows/publish-package.yml` — but you don't need that for in-process embedding.)*

---

## 3. Wire it up (DI) — your app supplies the AI key

```csharp
using RevealAI.Engine;
using RevealAI.Engine.Llm;

builder.Services.AddRevealAiEngine(new LlmOptions
{
    Provider = LlmProvider.Anthropic,   // Anthropic | OpenAI | Ollama
    ApiKey   = builder.Configuration["MyAi:Key"]   // from YOUR config/Key Vault; empty => heuristic-only
    // Model / BaseUrl optional (BaseUrl required for Ollama or an OpenAI-compatible gateway)
});
```

That registers `DashboardAiService` and `SchemaIntrospectionService`. **No appsettings sections are
required** — connections are passed per call (§4). (There's also an `AddRevealAiEngine(IConfiguration)`
overload that binds `"Llm"` + `"Connections"` if you ever prefer config-driven connections.)

---

## 4. The connection comes from your app (Reveal)

Build a `ConnectionConfig` from your Reveal data source — the field names line up 1:1:

```csharp
using RevealAI.Engine.DataSources;

var conn = new ConnectionConfig
{
    Id       = "sales",                       // any stable id; becomes the data source id in the .rdash
    Type     = ConnectionType.AzureSqlServer, // SqlServer | AzureSqlServer | PostgreSql | MySql | Oracle | AmazonRedshift | Snowflake | ...
    Host     = revealDs.Host,
    Database = revealDs.Database,
    Username = revealCredentials.Username,
    Password = revealCredentials.Password,
    Port     = revealDs.Port,    // optional
    Schema   = revealDs.Schema   // optional
};
```
Pass `conn` on each call — it's used as-is and never persisted.

---

## 5. Quick start

```csharp
using RevealAI.Engine;
using RevealAI.Engine.Compilation;    // CompileResult, RdashOutput
using RevealAI.Engine.Introspection;  // SchemaIntrospectionService
using RevealAI.Engine.Recommendation; // RecommendationRequest
using RevealAI.Engine.Schema;         // DatasetSchema
using RevealAI.Engine.Spec;           // DashboardSpec, VisualizationSpec
```

### A. Introspect a live SQL table → recommend → generate
```csharp
// inject DashboardAiService svc, SchemaIntrospectionService introspect
List<string> tables = await introspect.ListDatasetsAsync(conn);                  // optional: table picker
DatasetSchema schema = await introspect.IntrospectAsync(conn, "dbo.Orders", 50); // columns + exact stats + 50 sample rows
RecommendationResult recs = await svc.RecommendAsync(schema,
    new RecommendationRequest { Guidance = "use Count instead of Sum" });        // Guidance optional (LLM only)

// ... optionally let the user edit recs.Visualizations ...

var spec = new DashboardSpec {
    Title = "Sales", Connection = conn, Dataset = "dbo.Orders",
    Visualizations = recs.Visualizations
};
CompileResult result = svc.Compile(spec, schema);
byte[] rdash = RdashOutput.ToRdashBytes(result.Document);   // save/return this
// or: string json = RdashOutput.ToJson(result.Document);
```

### B. One-shot ("just build me a dashboard")
```csharp
DatasetSchema schema = await introspect.IntrospectAsync(conn, "dbo.Orders", 50);
CompileResult result = await svc.BuildAsync(
    title: "Sales", connectionId: "", dataset: "dbo.Orders",
    schema: schema, options: null, ct: default, connection: conn);
byte[] rdash = RdashOutput.ToRdashBytes(result.Document);
```

### C. No live DB — you already have columns/sample rows
```csharp
var schema = svc.BuildSchema("Sales", columns: null, sampleRows: rows);  // rows = List<Dictionary<string,string?>>
var recs   = await svc.RecommendAsync(schema);
```

---

## 6. API reference

### `IServiceCollection.AddRevealAiEngine(...)` — namespace `RevealAI.Engine`
- `AddRevealAiEngine(LlmOptions llm, IEnumerable<ConnectionConfig>? connections = null)` — app supplies the key; pass connections inline per call.
- `AddRevealAiEngine(IConfiguration config)` — binds `"Llm"` + `"Connections"` sections instead.

### `DashboardAiService` — namespace `RevealAI.Engine`
| Method | Purpose |
|---|---|
| `DatasetSchema BuildSchema(string datasetName, IReadOnlyList<ColumnSchema>? columns, IReadOnlyList<Dictionary<string,string?>>? sampleRows)` | Build/infer a schema from columns and/or sample rows. |
| `Task<RecommendationResult> RecommendAsync(DatasetSchema schema, RecommendationRequest? options = null, CancellationToken ct = default)` | Ranked recommendations. |
| `CompileResult Compile(DashboardSpec spec, DatasetSchema schema)` | Compile specs → `RdashDocument` (deterministic). |
| `Task<CompileResult> BuildAsync(string title, string connectionId, string dataset, DatasetSchema schema, RecommendationRequest? options = null, CancellationToken ct = default, ConnectionConfig? connection = null)` | One-shot recommend + compile. |

### `SchemaIntrospectionService` — namespace `RevealAI.Engine.Introspection`
| Method | Purpose |
|---|---|
| `bool CanIntrospect(ConnectionType type)` | True for SQL Server / Azure SQL. |
| `Task<List<string>> ListDatasetsAsync(ConnectionConfig conn / string connectionId, ct)` | List tables/views. |
| `Task<DatasetSchema> IntrospectAsync(ConnectionConfig conn / string connectionId, string dataset, int sampleRows = 5, ct)` | Columns + exact stats + sample rows. |

### `RdashOutput` (static) — namespace `RevealAI.Engine.Compilation`
`byte[] ToRdashBytes(RdashDocument)` · `string ToJson(RdashDocument)`.

### `CompileResult` — `RdashDocument Document` · `List<string> Warnings` (surface these — they list any skipped/invalid viz).

---

## 7. Data model & enums (namespace `RevealAI.Engine.Spec` / `.Schema` / `.DataSources`)

- **`DatasetSchema`**: `Name`, `List<ColumnSchema> Columns`, `List<Dictionary<string,string?>> SampleRows`, `long? RowCount`, `bool StatsAreEstimates`.
- **`ColumnSchema`**: `Name`, `DataType`, `SemanticTag`, `Nullable`, `DistinctCount?`, `NonNullCount?`, `NullFraction?`, `Min?`, `Max?`, `IsInteger`, `IsLikelyIdentifier`, `IsLikelyCategorical`, `SampleValues`; computed `IsMeasure`/`IsDimension`/`IsTemporal`.
- **`VisualizationSpec`**: `Title`, `VizType`, `List<FieldBinding> Bindings`, `Rationale?`, `ColumnSpan`, `RowSpan`, `Score`.
- **`FieldBinding`**: `Field`, `FieldRole Role`, `AggregationKind Aggregation`, `DateGrain DateGrain`.
- **`DashboardSpec`**: `Title`, `Description?`, `ConnectionId`, `ConnectionConfig? Connection`, `Dataset`, `List<VisualizationSpec> Visualizations`.
- **`RecommendationRequest`**: `int MaxRecommendations = 6`, `string? Guidance`. **`RecommendationResult`**: `Visualizations`, `Source`, `Warnings`.
- **`ConnectionConfig`**: `Id`, `Title`, `ConnectionType Type`, `Host?`, `Port?`, `Database?`, `Schema?`, `Url?`, `IsAnonymous`, `Username?`, `Password?`.

Enums:
- `DataType`: `Text, Number, Date, DateTime, Boolean`
- `SemanticTag`: `None, Identifier, Currency, Percentage, Latitude, Longitude, Geography, HighCardinality`
- `VizType` (compiled): `Grid, ColumnChart, BarChart, LineChart, AreaChart, SplineChart, PieChart, DoughnutChart, FunnelChart, ScatterChart, BubbleChart, KpiTarget` (`Pivot` modeled but not yet built)
- `FieldRole`: `Label, Value, Category, Column, Row, XAxis, YAxis, Target`
- `AggregationKind`: `None, Sum, Average, Count, CountDistinct, Min, Max`
- `DateGrain`: `None, Year, Quarter, Month, Day`
- `ConnectionType`: `SqlServer, AzureSqlServer, MySql, PostgreSql, Oracle, AmazonRedshift, Snowflake, GoogleBigQuery, Excel, Csv, Rest`
- `LlmProvider` (namespace `RevealAI.Engine.Llm`): `Anthropic, OpenAI, Ollama`

---

## 8. How chart decisions are made (deterministic, so output is predictable)

- **Identifiers** (`*id`/`*key`/`*code`, or integer with distinct ≈ row count) → counted, never summed.
- **Numeric codes/ordinals** (`status`/`type`/`rating`/… or a small distinct integer set) → grouping dimension, not a measure.
- **Aggregation**: ratios/rates/per-unit (`rate`/`ratio`/`ltv`/`price`/… or values in [0,1]) → **Average**; additive quantities (`amount`/`balance`/`qty`/…) and currency → **Sum**; ids/codes/non-numeric → **Count**.
- **Dimensions** must be ≤50 distinct, ≥2 distinct, <50% null to be used for grouping.
- **Pies** only for an additive (Sum) measure with 3–8 categories.
- **Date axes** are auto-bucketed (Year/Quarter/Month/Day from the span).
- **Scatter/bubble** plot one point per entity (need a label) — skipped otherwise.
- For introspected sources the **DB's declared types win** (a numeric-looking varchar id stays text).

With an LLM key, the model overlays headline-measure choice, better titles, and a less-redundant set —
the same profiling stats are handed to it.

### Presentation defaults applied at compile
Large Number Formatting = Auto on numeric measures & grid columns; grids page at 100 rows (server-side
processing enabled on DB items); date axes bucketed; pies suppressed for non-additive / 2-value splits.

---

## 9. Gotchas & limits

- **Credentials are not embedded in the `.rdash`.** It stores the data-source definition (host/db/table);
  the **Reveal SDK server resolves credentials at view time** via its `IRVDataSourceProvider`. The
  username/password you pass are for *generation-time* introspection — make sure your Reveal server has
  its own credentials for the same source so the rendered dashboard can fetch data.
- **Introspection is SQL Server / Azure SQL only** today. Other `ConnectionType`s still work for
  *generation* (you supply the schema/sample rows); add an `ISchemaIntrospector` to extend.
- **`Pivot`** is modeled but not compiled yet (skipped with a warning).
- **`CompileResult.Warnings`** lists anything skipped — log/surface it.
- Host must be on **Reveal.Sdk.Dom >= 0.1.668-beta**.
