# Updates — Multi-Source Data Workbench (2026-07-21)

This wave turns the single-database demo into a multi-datasource workbench. It builds on the
earlier migration from Azure SQL Server to SQLite. Server (`RevealServerNorthwind`) and client
(`RevealClientNorthwind`) changed together; both build clean and the flows below were verified
end-to-end against a running server.

## 1. Multi-source architecture (folder per source)

- New layout: `Data/{sourceId}/{sourceId}.sqlite` + `Dashboards/{sourceId}/*.rdash`.
  A one-time startup migration moves the legacy flat layout into the `northwind` source
  (idempotent — second start is a no-op).
- New `Services/SourceRegistry.cs` — scans `Data/*/` and resolves every per-source path.
- The client sends the active source via an **`X-DataSource` header** on every request
  (fetch wrapper, Reveal SDK headers provider, and the `@revealbi/api` chat client all read
  it per-request from `src/lib/dataSource.ts`).
- `Sdk/UserContextProvider.cs` puts the source into the Reveal user context
  (header → share-token claim → default). Null-safe for the engine's background flows,
  which call it with **no HttpContext**.
- `Sdk/DataSourceProvider.cs` resolves the `.sqlite` file per request:
  datasource Id = known sourceId → legacy aliases (`NorthwindSql`, `sqlServer`) →
  user-context source → default.
- New `Sdk/DashboardProvider.cs` (`IRVDashboardProvider`) — loads/saves dashboards from
  `Dashboards/{sourceId}/`, with a cross-source fallback search for legacy links.
- All `/sql/*`, `/dashboards/*`, `/ai/visualizations/*` endpoints are source-scoped.
- New endpoints: `GET /sources` (list with kind/tables/dashboards) and
  `DELETE /sources/{id}?deleteDashboards=true` (cascades AI metadata, catalog, share links;
  refuses to delete `northwind`).
- Client: header **source switcher** dropdown (`SourceSwitcher` + `SourceContext`);
  switching remounts the routed page so Dashboards, Visualization Catalog, AI Assistant,
  and Connections all rescope automatically.

## 2. Connections page browses EVERYTHING

- The AI whitelist no longer filters `/sql/objects` / `/sql/rowcounts` — the Connections
  tree shows **all** tables and views. The whitelist restricts only the AI (see #4).
- `Sdk/DataSourceItemFilter.cs` is now permissive (kept as a hook point).

## 3. Excel upload → SQLite import (worksheet tabs = tables)

- New `Services/ExcelToSqliteImporter.cs` (ExcelDataReader): `POST /data/upload` saves the
  workbook to `Data/{sourceId}/` and imports **every worksheet tab as a table** in a new
  `{sourceId}.sqlite`. Column types inferred per column; **dates stored as Unix-epoch
  seconds** (required by Reveal's SQLite connector) and numeric-looking strings stay text
  (zip codes keep leading zeros). Sheet/column names sanitized; empty sheets skipped with
  warnings.
- Client: drag-drop upload zone on `/connections`; after import the new source becomes
  active and its tabs appear as tables.
- The `/data` static mount now serves **only** `.xlsx/.xls` (content-type whitelist), so
  the per-source JSON artifacts are not anonymously downloadable.

## 4. Dynamic AI catalog (pick tables for the AI on /connections)

- Selection persists per source at `Data/{sourceId}/ai-selection.json` (seeded once from
  `Sqlite:CatalogObjects`); `Services/AiCatalogService.cs` compiles all selections into the
  Restricted `Reveal/Metadata/catalog.json` (one SQLITE datasource per source,
  **catalog id = sourceId**). The catalog file is re-read per call, so changes apply
  without a restart.
- `GET /ai/catalog` (available + selected), `PUT /ai/catalog` (validates, rewrites the
  catalog, fire-and-forget `IMetadataService.RegenerateMetadataAsync`, returns 202; the
  client polls the existing `GET /api/reveal/ai/metadata/status`).
- Client: per-row **AI pill toggles** in the Connections tree with a sticky Apply/Reset
  bar and generation progress.

## 5. Suggested starter questions per source

- New `Services/SuggestedQuestionsService.cs`: one LLM call over the source's schema
  produces 6 starter questions, cached at `Data/{sourceId}/questions.json`; regenerated
  when the AI selection changes; deterministic template fallback when no API key.
- `GET /ai/suggestions`; the AI Assistant loads them (skeleton chips while loading) and
  its datasource id / labels now follow the active source.

## 6. Dashboard download (#8)

- Hover **Download** icon on each dashboard → OS-level Save As (`showSaveFilePicker` on
  Chromium; plain download elsewhere).
- **“Download a Copy”** button in the Save dialog → serializes and drops the `.rdash` in
  the Downloads folder without saving to the server.

## 7. Share links + public viewer with AI breakdown (#9)

- New `Services/ShareService.cs` (`Reveal/shares.json` registry): `POST /share` → GUID;
  **anonymous** `GET /share/{guid}` exchanges it for a **short-lived share JWT**
  (`Auth:ShareTokenHours`, default 2h) carrying `scope=share` + the sourceId.
- Middleware restricts share principals to read-style traffic (Reveal's own
  `/dashboard/...` POSTs allowed; app mutations return 403).
- New `Services/DashboardAnalyzer.cs`: dual-read of the `.rdash` (typed `Reveal.Sdk.Dom`
  + raw `Dashboard.json`) extracts per-viz facts — chart type, table, bound
  fields/aggregations, hidden fields, filters — then one LLM call writes a narrative;
  cached per dashboard, invalidated on save/delete. `GET /dashboards/{name}/analysis`
  + anonymous `GET /share/{guid}/analysis`.
- Client: **Share** in the dashboard overflow menu and hover actions → copyable-link
  dialog; new public route **`/share/{guid}`** renders the dashboard read-only with a
  **Breakdown tab** (theme-accented chart-type icons, field/filter chips, markdown
  narrative). Expired links never log out a signed-in tab.

## Fixes made along the way

- `PUT /ai/catalog` regeneration failed silently: `UserContextProvider` dereferenced a
  **null HttpContext** (the Reveal engine resolves user contexts without a request in
  background metadata flows) — now null-guarded, and the fire-and-forget tasks log faults.
- Removed the stale `RevealAI:MetadataManager` appsettings section (trailing-comma JSON
  bug) — the file catalog is authoritative.
- csproj: `Data\**\*` glob, ExcelDataReader + CodePages packages, dead item removed.

## Notes

- Legacy `Total Sales by Customer.rdash` fails `RdashDocument.Load` (it references a view
  that doesn't exist in the SQLite DB) and is skipped from listings — pre-existing.
- Share tokens can read other dashboards in the same source (documented demo trade-off).
- Rotate the OpenAI keys that were previously committed/circulated; the live key lives in
  the untracked `appsettings.json`.
