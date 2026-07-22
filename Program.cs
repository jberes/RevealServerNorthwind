using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.IO.Compression;
using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.AI;
using Reveal.Sdk.Dom;
using RevealExcel.Sdk;
using RevealSdk.Sdk;
using RevealSdk.Services;
using System.Text.Json.Serialization;
// RevealAI.Engine — schema → AI-recommended visualizations → compiled .rdash.
using RevealAI.Engine;
using RevealAI.Engine.Llm;
using RevealAI.Engine.DataSources;
using RevealAI.Engine.Introspection;
using RevealAI.Engine.Recommendation;
using RevealAI.Engine.Spec;
using RevealAI.Engine.Compilation;
// AI support — requires Reveal.Sdk.AspNetCore >= 1.8.4 and an OpenAI API key in appsettings.json
// using Reveal.Sdk.AI;

var builder = WebApplication.CreateBuilder(args);

// ---- Folder-per-source layout: migrate + build the AI metadata catalog ----
// Layout: Data/{sourceId}/{sourceId}.sqlite and Dashboards/{sourceId}/*.rdash.
// A one-time migration moves the legacy flat layout (Data/northwind.sqlite +
// Dashboards/*.rdash) into the "northwind" source. Which tables each source
// exposes to the AI lives in Data/{sourceId}/ai-selection.json (seeded from
// Sqlite:CatalogObjects for northwind) and is compiled into the Restricted
// catalog.json by AiCatalogService — the /sql browsing endpoints are NOT
// filtered by it (the whitelist limits only the AI).
MigrateLegacyLayout(builder.Environment.ContentRootPath);

var sourceRegistry = new SourceRegistry(builder.Environment);
var aiCatalog = new AiCatalogService(sourceRegistry, builder.Environment);

// Seed northwind's AI selection from appsettings on first run (seed-only; the
// selection file is the source of truth afterwards).
var seedObjects = builder.Configuration.GetSection("Sqlite").Get<SqliteOptions>()?.CatalogObjects
                  ?? Array.Empty<string>();
if (seedObjects.Length > 0)
    aiCatalog.SeedSelectionIfMissing(SourceRegistry.DefaultSourceId, seedObjects);

aiCatalog.RebuildCatalogJson();
var catalogPath = aiCatalog.CatalogPath;
// ---------------------------------------------------------------------------

builder.Services.AddControllers().AddReveal(revealBuilder =>
{
    // SQLite is a local file and needs no credentials, so there is no
    // authentication provider (the Azure SQL Server connection was removed).
    revealBuilder
        .AddDataSourceProvider<DataSourceProvider>()
        .AddDashboardProvider<DashboardProvider>()
        .AddObjectFilter<DataSourceItemFilter>()
        .AddUserContextProvider<UserContextProvider>()
        .DataSources.RegisterSQLite();

    revealBuilder
        .AddSettings(settings =>
        {
            settings.License = "eyJhbGciOiJQUzUxMiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3NzMyNDA0NzIsIm5iZiI6MTc3MzI0MDQ3MiwiaWQiOiJhMFdWTTAwMDAwNkp5clIyQVMiLCJwcm9kdWN0X2NvZGUiOiJBOCIsInByb2R1Y3RfdmVyc2lvbiI6IjcwIiwicHJvZHVjdF9wbGFuIjoicmV2ZWFsLXBybyIsInNlcnZpY2VfZW5kX2RhdGUiOiIyMDI3LTAzLTExVDAwOjAwOjAwLjAwMDAwMDBaIiwic2VydmljZV9sZXZlbCI6IlByaW9yaXR5In0.HrWe-xkY48l6euIT63lE9wKx7ye4KH0GRzBD3Bl9xfCP6hRNeR3yyxoiVI54zO81y3jyb9YMeSvh-8pjwCfL_0c-vNJA2vBRk-3gj-EjjSGljINkJlMDGQIklQOEtbn_8YEiSpNKlNTRHDAfqYikhGBYRs9HrKE4eZKEAVgJKBi0jv1Kp8ztVHlLvbmOddC3p-TwYH-QXiag4xhh4oiQydKbKaCXZx1CAsiwrUJ-DEz0k85U14YsKPouMyCy3vZxshvcodgIKxREa9OnQNY3qGGF19fhsLr8D2o43zSplKO5knAHJVUZVwEDgsF-CsvGwqrc3N6JKSEoB0PUQiycaQ";
            settings.LocalFileStoragePath = "Data";
        });
});

builder.Services.Configure<SqliteOptions>(
    builder.Configuration.GetSection("Sqlite"));

// The registry/catalog instances created above (needed before Build() for the
// startup migration + catalog rebuild) are the app-wide singletons.
builder.Services.AddSingleton(sourceRegistry);
builder.Services.AddSingleton(aiCatalog);
builder.Services.AddSingleton<ExcelToSqliteImporter>();
builder.Services.AddSingleton<SuggestedQuestionsService>();
builder.Services.AddSingleton<ShareService>();
builder.Services.AddSingleton<DashboardAnalyzer>();

// ---- Authentication (JWT bearer) ---------------------------------------
// The client logs in at /auth/login with credentials from the "Auth" config
// section and receives a signed JWT. Every data/Reveal endpoint then requires a
// valid token (enforced by the fallback authorization policy below), so the API
// cannot be used without authenticating — the login UI alone is not the gate.
var authKey = builder.Configuration["Auth:JwtKey"]
              ?? throw new InvalidOperationException("Auth:JwtKey is not configured.");
var authIssuer = builder.Configuration["Auth:Issuer"] ?? "RevealNorthwindDemo";
var authAudience = builder.Configuration["Auth:Audience"] ?? "RevealNorthwindDemo";
var signingKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
    System.Text.Encoding.UTF8.GetBytes(authKey));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authIssuer,
            ValidateAudience = true,
            ValidAudience = authAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

// Every endpoint requires an authenticated user unless it opts out with
// .AllowAnonymous() (only /auth/login does). Static files served via
// UseStaticFiles are middleware, not endpoints, so they remain reachable.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
// -------------------------------------------------------------------------

// ---- AI Support --------------------------------------------------------
// Datasources come from the catalog.json generated above; the OpenAI key/model
// are read from appsettings.json under RevealAI:OpenAI.
builder.Services.AddRevealAI()
    .UseMetadataCatalogFile(catalogPath)
    .AddOpenAI(settings =>
    {
        settings.ApiKey = builder.Configuration["RevealAI:OpenAI:ApiKey"] ?? "";
        settings.Model = builder.Configuration["RevealAI:OpenAI:Model"] ?? "gpt-4.1";
    });
// -------------------------------------------------------------------------

// ---- RevealAI.Engine ---------------------------------------------------
// Turns a SQL table/view schema into AI-recommended visualizations and compiles
// them into a .rdash. Reuses the same OpenAI key as the Reveal AI chat above;
// with no key it falls back to the deterministic heuristic recommender.
// Connections are passed inline per request (see the /ai/visualizations/* endpoints).
builder.Services.AddRevealAiEngine(new LlmOptions
{
    Provider = LlmProvider.OpenAI,
    ApiKey = builder.Configuration["RevealAI:OpenAI:ApiKey"] ?? "",
    Model = builder.Configuration["RevealAI:OpenAI:Model"]   // optional; engine defaults if null
});

// Serialize/deserialize enums (VizType, FieldRole, Aggregation, DateGrain) as strings
// so the VisualizationSpec round-trips cleanly to/from the React client.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// -------------------------------------------------------------------------

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
  options.AddPolicy("AllowAll",
    policy => policy
      .AllowAnyOrigin()
      .AllowAnyHeader()
      .AllowAnyMethod()
      .WithExposedHeaders("*")
  );
});

var app = builder.Build();

// Ensure required directories exist (critical for Azure App Service where they may not be present)
var imagesDir = Path.Combine(builder.Environment.ContentRootPath, "Images");
var dashboardsDir = Path.Combine(builder.Environment.ContentRootPath, "Dashboards");
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "Data");
Directory.CreateDirectory(imagesDir);
Directory.CreateDirectory(dashboardsDir);
Directory.CreateDirectory(dataDirectory);

// CORS must be before other middleware
app.UseCors("AllowAll");

// Explicit OPTIONS handler for preflight requests (Azure App Service compatibility)
app.Use(async (context, next) =>
{
    if (context.Request.Method == "OPTIONS")
    {
        context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
        context.Response.Headers.Append("Access-Control-Allow-Methods", "*");
        context.Response.Headers.Append("Access-Control-Allow-Headers", "*");
        context.Response.StatusCode = 200;
        await context.Response.CompleteAsync();
        return;
    }
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(imagesDir),
    RequestPath = "/Images",
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
        ctx.Context.Response.Headers.Append("Access-Control-Allow-Headers", "*");
        ctx.Context.Response.Headers.Append("Access-Control-Allow-Methods", "*");
    }
});

app.UseSwagger();
app.UseSwaggerUI();

// Resolve the ACTIVE SOURCE for an endpoint request: X-DataSource header (set
// globally by the client) ?? sourceId claim (share tokens) ?? the default source.
SourceInfo ResolveSource(HttpContext http) =>
    sourceRegistry.Resolve(http.Request.Headers["X-DataSource"].FirstOrDefault()
                           ?? http.User.FindFirst("sourceId")?.Value);

// Serve ONLY Excel workbooks from the Data tree at /data/{sourceId}/{file}.xlsx.
// The content-type whitelist matters: per-source JSON artifacts (ai-selection.json,
// questions.json) live in the same folders and must NOT be anonymously downloadable.
var excelOnlyTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider(
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".xls"] = "application/vnd.ms-excel"
    });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataDirectory),
    RequestPath = "/data",
    ContentTypeProvider = excelOnlyTypes,
    ServeUnknownFileTypes = false,
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
    }
});


app.MapGet("/dashboards/names", (HttpContext http) =>
{
    try
    {
        var folderPath = ResolveSource(http).DashboardsDir;
        // *.rdash only — {name}.analysis.json caches live alongside the dashboards.
        var files = Directory.Exists(folderPath)
            ? Directory.GetFiles(folderPath, "*.rdash")
            : Array.Empty<string>();

        var fileNames = files.Select(file =>
        {
            try
            {
                return new DashboardNames
                {
                    DashboardFileName = Path.GetFileNameWithoutExtension(file),
                    DashboardTitle = RdashDocument.Load(file).Title
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error Reading FileData {file}: {ex.Message}");
                return null;
            }
        }).Where(fileData => fileData != null).ToList();

        return Results.Ok(fileNames);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error Reading Directory : {ex.Message}");
        return Results.Problem("An unexpected error occurred while processing the request.");
    }

}).Produces<IEnumerable<DashboardNames>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status500InternalServerError);

app.MapGet("/dashboards/{name}/thumbnail", (string name, HttpContext http) =>
{
    var path = Path.Combine(ResolveSource(http).DashboardsDir, name + ".rdash");
    if (File.Exists(path))
    {
        var dashboard = new Dashboard(path);
        var info = dashboard.GetInfo(Path.GetFileNameWithoutExtension(path));
        return TypedResults.Ok(info);
    }
    else
    {
        return Results.NotFound();
    }
});

app.MapGet("dashboards/visualizations", (HttpContext http) =>
{
    try
    {
        var allVisualizationChartInfos = new List<VisualizationChartInfo>();
        var dashDir = ResolveSource(http).DashboardsDir;
        var dashboardFiles = Directory.Exists(dashDir)
            ? Directory.GetFiles(dashDir, "*.rdash")
            : Array.Empty<string>();

        foreach (var filePath in dashboardFiles)
        {
            try
            {
                var document = RdashDocument.Load(filePath);
                foreach (var viz in document.Visualizations)
                {
                    try
                    {
                        var chartInfo = new VisualizationChartInfo
                        {
                            DashboardFileName = Path.GetFileNameWithoutExtension(filePath),
                            DashboardTitle = document.Title,
                            VizId = viz.Id,
                            VizTitle = viz.Title,
                            VizChartType = viz.ChartType.ToString(),
                        };
                        allVisualizationChartInfos.Add(chartInfo);
                    }
                    catch (Exception vizEx)
                    {
                        Console.WriteLine($"Error processing visualization {viz.Id} in file {filePath}: {vizEx.Message}");
                    }
                }
            }
            catch (Exception fileEx)
            {
                Console.WriteLine($"Error processing file {filePath}: {fileEx.Message}");
            }
        }
        return Results.Ok(allVisualizationChartInfos);
    }
    catch (Exception ex)
    {
        return Results.Problem($"An error occurred: {ex.Message}");
    }

}).Produces<IEnumerable<VisualizationChartInfo>>(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status500InternalServerError);

// ---------------------------------------------------------------------------
// Data sources: list, upload (Excel → per-source SQLite import), delete.
// ---------------------------------------------------------------------------

// GET /sources — every data source (folder under Data/ containing a .sqlite).
app.MapGet("/sources", () =>
{
    try
    {
        sourceRegistry.Refresh();
        var list = sourceRegistry.GetSources().Select(s =>
        {
            int tables = 0;
            try
            {
                tables = AiCatalogService.ListObjects(s.SqlitePath).Count;
            }
            catch { /* unreadable DB — still list the source */ }
            var dashboards = Directory.Exists(s.DashboardsDir)
                ? Directory.GetFiles(s.DashboardsDir, "*.rdash").Length
                : 0;
            return new
            {
                id = s.SourceId,
                name = s.SourceId,
                kind = s.Kind,
                tables,
                dashboards,
                workbookFile = s.WorkbookPath is null ? null : Path.GetFileName(s.WorkbookPath)
            };
        }).ToList();
        return Results.Ok(list);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error listing sources: {ex.Message}");
        return Results.Problem("An unexpected error occurred while listing data sources.");
    }
}).Produces(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// POST /data/upload — upload an Excel workbook and import it into a NEW source:
// every worksheet tab becomes a table in Data/{sourceId}/{sourceId}.sqlite.
// The original workbook is kept alongside for preview/download. The new source is
// NOT auto-added to the AI catalog (the user opts tables in from /connections).
app.MapPost("/data/upload", async (IFormFile file, ExcelToSqliteImporter importer, CancellationToken ct) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { message = "No file provided." });

    var fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (fileExtension is not (".xlsx" or ".xls"))
        return Results.BadRequest(new { message = "Only Excel files (.xlsx, .xls) are allowed." });

    var sourceId = SourceRegistry.Sanitize(Path.GetFileNameWithoutExtension(file.FileName)).ToLowerInvariant();
    if (sourceRegistry.Find(sourceId) is not null)
        return Results.Conflict(new { message = $"A data source named '{sourceId}' already exists. Delete it first or rename the file." });

    var src = sourceRegistry.Create(sourceId);
    var dataDir = Path.GetDirectoryName(src.SqlitePath)!;
    var workbookPath = Path.Combine(dataDir, Path.GetFileName(file.FileName));
    try
    {
        await using (var stream = new FileStream(workbookPath, FileMode.Create))
        {
            await file.CopyToAsync(stream, ct);
        }

        var import = await importer.ImportAsync(workbookPath, src.SqlitePath, ct);
        sourceRegistry.Refresh();

        return Results.Ok(new
        {
            sourceId,
            fileName = Path.GetFileName(workbookPath),
            tables = import.Tables.Select(t => new { name = t.Name, rows = t.Rows, columns = t.Columns }),
            warnings = import.Warnings
        });
    }
    catch (Exception ex)
    {
        // Roll back the half-created source folder so a retry isn't blocked by 409.
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
            sourceRegistry.Refresh();
        }
        catch { /* best-effort cleanup */ }
        Console.WriteLine($"Error importing '{file.FileName}': {ex.Message}");
        return Results.BadRequest(new { message = $"Could not import the workbook: {ex.Message}" });
    }
}).Accepts<IFormFile>("multipart/form-data")
  .Produces<object>(StatusCodes.Status200OK)
  .Produces<object>(StatusCodes.Status400BadRequest)
  .Produces<object>(StatusCodes.Status409Conflict)
  .ProducesProblem(StatusCodes.Status500InternalServerError)
  .DisableAntiforgery();

// DELETE /sources/{sourceId}?deleteDashboards=true — remove a source entirely:
// its data folder, (by default) its dashboards, its AI selection/metadata.
app.MapDelete("/sources/{sourceId}", async (string sourceId, bool? deleteDashboards,
    Reveal.Sdk.AI.AspNetCore.Services.IMetadataService metadataService,
    Reveal.Sdk.AI.Metadata.IMetadataManager metadataManager, ShareService shares) =>
{
    if (string.Equals(sourceId, SourceRegistry.DefaultSourceId, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = "The built-in 'northwind' source cannot be deleted." });
    if (sourceRegistry.Find(sourceId) is null)
        return Results.NotFound();

    try
    {
        // Remove AI metadata + catalog entry first (the selection file dies with the folder).
        try { await metadataService.RemoveMetadataAsync(sourceId, null); }
        catch (Exception ex) { Console.WriteLine($"[Sources] metadata removal for '{sourceId}': {ex.Message}"); }

        SqliteConnection.ClearAllPools();   // Windows: pooled handles keep the .sqlite locked
        sourceRegistry.Delete(sourceId, deleteDashboards ?? true);
        aiCatalog.RebuildCatalogJson();
        await metadataManager.Reload();     // drop the deleted source from the in-memory list
        shares.RemoveForSource(sourceId);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error deleting source '{sourceId}': {ex.Message}");
        return Results.Problem($"Error deleting source: {ex.Message}");
    }
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status400BadRequest)
  .Produces(StatusCodes.Status404NotFound)
  .ProducesProblem(StatusCodes.Status500InternalServerError);


// Check if a dashboard name already exists (within the active source)
app.MapGet("/isduplicatename/{name}", (string name, HttpContext http) =>
{
    var folderPath = ResolveSource(http).DashboardsDir;
    return Results.Ok(File.Exists(Path.Combine(folderPath, $"{name}.rdash")));
});

// GET /dashboards/{name} — return the raw .rdash file bytes.
// Used by the Visualization Catalog, which fetches a source dashboard's bytes and
// parses them with RdashDocument.load(blob) to import individual visualizations
// into a new composite dashboard. Without this, the route only had POST/PUT/DELETE
// handlers, so a GET matched the template but no method → 405 Method Not Allowed.
// Literal segments ("names", "visualizations") outrank the {name} parameter in
// routing, so those GET endpoints above still take precedence.
app.MapGet("/dashboards/{name}", (string name, HttpContext http) =>
{
    var filePath = Path.Combine(ResolveSource(http).DashboardsDir, $"{name}.rdash");
    if (!File.Exists(filePath)) return Results.NotFound();
    return Results.File(File.ReadAllBytes(filePath), "application/octet-stream", $"{name}.rdash");
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound);

// POST /dashboards/{name} — create new dashboard (in the active source)
// Accepts either raw rdash bytes (ZIP) from the Reveal SDK or DOM JSON from the
// AI Assistant. DOM JSON is parsed by the Reveal DOM and saved as a real .rdash.
app.MapPost("/dashboards/{name}", async (HttpRequest request, string name, HttpContext http) =>
{
    var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    var dashDir = ResolveSource(http).DashboardsDir;
    Directory.CreateDirectory(dashDir);
    var filePath = Path.Combine(dashDir, $"{name}.rdash");
    try
    {
        SaveDashboardDocument(ms.ToArray(), filePath);
        DeleteAnalysisCache(filePath);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error saving dashboard '{name}': {ex.Message}");
        return Results.Problem($"Error saving dashboard: {ex.Message}");
    }
}).Produces(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// PUT /dashboards/{name} — overwrite an existing dashboard file
// Accepts either raw rdash bytes (ZIP) from the Reveal SDK or DOM JSON from the AI Assistant.
app.MapPut("/dashboards/{name}", async (HttpRequest request, string name, HttpContext http) =>
{
    var filePath = Path.Combine(ResolveSource(http).DashboardsDir, $"{name}.rdash");
    if (!File.Exists(filePath)) return Results.NotFound();
    var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    try
    {
        SaveDashboardDocument(ms.ToArray(), filePath);
        DeleteAnalysisCache(filePath);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error saving dashboard '{name}': {ex.Message}");
        return Results.Problem($"Error saving dashboard: {ex.Message}");
    }
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// DELETE /dashboards/{name} — remove a dashboard file (+ analysis cache + share links)
app.MapDelete("/dashboards/{name}", (string name, HttpContext http, ShareService shares) =>
{
    var src = ResolveSource(http);
    var filePath = Path.Combine(src.DashboardsDir, $"{name}.rdash");
    if (!File.Exists(filePath)) return Results.NotFound();
    File.Delete(filePath);
    DeleteAnalysisCache(filePath);
    shares.RemoveForDashboard(src.SourceId, name);
    return Results.Ok();
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound);

// ---------------------------------------------------------------------------
// SQLite browsing endpoints (used by the client Connections page).
// The database file path comes from the "Sqlite" section of appsettings.json
// (resolved to sqliteDbPath above). SQLite has no host, schema, or credentials.
// The endpoints close over sqliteDbPath and catalogFilter.
// ---------------------------------------------------------------------------

// GET /sql/connection — connection metadata for the tree header (active source)
app.MapGet("/sql/connection", (HttpContext http) =>
{
    var src = ResolveSource(http);
    return Results.Ok(new
    {
        sourceId = src.SourceId,
        database = Path.GetFileName(src.SqlitePath),
        path = src.SqlitePath,
        provider = "SQLite"
    });
});

// GET /sql/objects — ALL tables and views in the active source. Deliberately
// unfiltered: the AI whitelist (ai-selection.json → catalog.json) limits only
// what the AI assistant can see, never what the Connections page can browse.
app.MapGet("/sql/objects", async (HttpContext http) =>
{
    try
    {
        await using var conn = new SqliteConnection(SqliteConnString(ResolveSource(http).SqlitePath));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT name, type FROM sqlite_master
                            WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%'
                            ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync();

        var objects = new List<object>();
        while (await reader.ReadAsync())
        {
            objects.Add(new
            {
                name = reader.GetString(0),
                type = reader.GetString(1) == "view" ? "view" : "table"
            });
        }
        return Results.Ok(objects);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error listing SQLite objects: {ex.Message}");
        return Results.Problem($"Error listing SQLite objects: {ex.Message}");
    }
}).Produces(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// GET /sql/data/{name}?top=N — return rows + column metadata for a table/view
app.MapGet("/sql/data/{name}", async (string name, int? top, HttpContext http) =>
{
    try
    {
        await using var conn = new SqliteConnection(SqliteConnString(ResolveSource(http).SqlitePath));
        await conn.OpenAsync();

        // Verify the object exists. This also guards the interpolated name below:
        // only a name that matches a real catalog entry is ever queried.
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = $name AND type IN ('table','view')";
            check.Parameters.AddWithValue("$name", name);
            var exists = Convert.ToInt32(await check.ExecuteScalarAsync() ?? 0);
            if (exists == 0)
                return Results.NotFound(new { message = $"Object '{name}' not found." });
        }

        // Date/datetime columns are STORED as Unix-epoch seconds (required by
        // Reveal's SQLite connector) — surface them as real dates in the preview.
        // PRAGMA table_info gives the DECLARED types; anything date-ish is
        // converted to an ISO string and reported as DateTime for the grid.
        var dateColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info(\"{name.Replace("\"", "\"\"")}\")";
            await using var pr = await pragma.ExecuteReaderAsync();
            while (await pr.ReadAsync())
            {
                var declared = pr.IsDBNull(2) ? "" : pr.GetString(2);
                if (declared.Contains("date", StringComparison.OrdinalIgnoreCase) ||
                    declared.Contains("time", StringComparison.OrdinalIgnoreCase))
                    dateColumns.Add(pr.GetString(1));
            }
        }

        var limit = top is > 0 and <= 100000 ? top.Value : 1000;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{name.Replace("\"", "\"\"")}\" LIMIT {limit}";
        await using var reader = await cmd.ExecuteReaderAsync();

        var columns = new List<object>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var colName = reader.GetName(i);
            columns.Add(new
            {
                name = colName,
                type = dateColumns.Contains(colName) ? "DateTime" : reader.GetFieldType(i).Name
            });
        }

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var colName = reader.GetName(i);
                if (reader.IsDBNull(i))
                {
                    row[colName] = null;
                }
                else if (dateColumns.Contains(colName) && reader.GetValue(i) is long epoch)
                {
                    row[colName] = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime
                        .ToString("yyyy-MM-ddTHH:mm:ss");
                }
                else
                {
                    row[colName] = reader.GetValue(i);
                }
            }
            rows.Add(row);
        }

        return Results.Ok(new { name, columns, rowCount = rows.Count, rows });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error reading '{name}': {ex.Message}");
        return Results.Problem($"Error reading '{name}': {ex.Message}");
    }
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// GET /sql/rowcounts — { name: rowCount } for every table/view in the active
// source. SQLite has no stored statistics, so each is a COUNT(*). Best-effort:
// objects whose count can't be obtained are simply omitted.
app.MapGet("/sql/rowcounts", async (HttpContext http, CancellationToken ct) =>
{
    var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    try
    {
        await using var conn = new SqliteConnection(SqliteConnString(ResolveSource(http).SqlitePath));
        await conn.OpenAsync(ct);

        var names = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT name FROM sqlite_master
                                WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%'";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                names.Add(r.GetString(0));
            }
        }

        foreach (var n in names)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM \"{n.Replace("\"", "\"\"")}\"";   // n is a real catalog name
                var v = await cmd.ExecuteScalarAsync(ct);
                if (v is not null && v != DBNull.Value) counts[n] = Convert.ToInt64(v);
            }
            catch { /* best-effort chips */ }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error computing row counts: {ex.Message}");
    }
    return Results.Ok(counts);   // partial/empty map is fine
}).Produces(StatusCodes.Status200OK);

// ---------------------------------------------------------------------------
// AI visualization endpoints (RevealAI.Engine) — used by the Connections page's
// "Generate Visualizations" flow. The SQLite connection targets the request's
// ACTIVE SOURCE; the generated .rdash embeds the sourceId as its datasource Id,
// which DataSourceProvider resolves back to the right .sqlite at view time.
// ---------------------------------------------------------------------------

// Default guidance handed to the LLM recommender. The engine's validator requires
// the PRIMARY grouping dimension of category/part-to-whole charts to use role
// "Label" (it treats "Category" as only a secondary series breakdown), so without
// this nudge the model's column/bar/pie suggestions get dropped. This steers the
// model toward valid, varied specs. User-supplied guidance is appended after it.
const string DefaultVizGuidance =
    "Role rules (critical): for ColumnChart, BarChart, LineChart, AreaChart, SplineChart, " +
    "PieChart, DoughnutChart and FunnelChart, the primary grouping dimension MUST use role " +
    "'Label' (never 'Category'). 'Category' is only for an optional secondary series breakdown. " +
    "Each of these charts must have exactly one 'Label' plus one aggregated 'Value'. " +
    "Prefer low-cardinality categorical columns (e.g. exchange, classification, currency, status, " +
    "type, rating) as the Label, and a genuine additive amount as the Value. " +
    "If a date/datetime column exists, include at least one time series (LineChart or AreaChart) " +
    "with the date as the Label. For a Grid, list several representative columns as bindings " +
    "(role 'Label' for text/date columns, role 'Value' with an aggregation for numeric columns) — " +
    "never return a Grid with an empty bindings array. " +
    "Aim for a varied set: a Grid plus several distinct category charts.";

static string ComposeGuidance(string? userGuidance) =>
    string.IsNullOrWhiteSpace(userGuidance)
        ? DefaultVizGuidance
        : $"{DefaultVizGuidance}\nAdditional user guidance (takes priority): {userGuidance}";

// POST /ai/visualizations/recommend — introspect a table/view and return ranked,
// editable visualization suggestions.
app.MapPost("/ai/visualizations/recommend",
    async (RecommendRequest req, SchemaIntrospectionService introspect, DashboardAiService svc,
           HttpContext http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req?.Dataset))
        return Results.BadRequest(new { message = "A dataset (table or view name) is required." });
    try
    {
        var src = ResolveSource(http);
        var conn = BuildAiConnection(src.SourceId, src.SqlitePath);
        var schema = await introspect.IntrospectAsync(conn, req.Dataset, 50, ct);
        var recs = await svc.RecommendAsync(schema,
            new RecommendationRequest { Guidance = ComposeGuidance(req.Guidance) }, ct);
        return Results.Ok(new
        {
            dataset = req.Dataset,
            source = recs.Source,
            warnings = recs.Warnings,
            visualizations = recs.Visualizations
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[AI Recommend] {req?.Dataset}: {ex.Message}");
        return Results.Problem($"Could not analyze '{req?.Dataset}': {ex.Message}");
    }
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status400BadRequest)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// POST /ai/visualizations/generate — compile the (edited) specs into a dashboard
// and return its Reveal DOM JSON for live rendering on the client.
app.MapPost("/ai/visualizations/generate",
    async (GenerateRequest req, SchemaIntrospectionService introspect, DashboardAiService svc,
           HttpContext http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req?.Dataset))
        return Results.BadRequest(new { message = "A dataset (table or view name) is required." });
    if (req.Visualizations is null || req.Visualizations.Count == 0)
        return Results.BadRequest(new { message = "Select at least one visualization to generate." });
    try
    {
        var src = ResolveSource(http);
        var conn = BuildAiConnection(src.SourceId, src.SqlitePath);
        var schema = await introspect.IntrospectAsync(conn, req.Dataset, 50, ct);
        var spec = new DashboardSpec
        {
            Title = string.IsNullOrWhiteSpace(req.Title) ? req.Dataset : req.Title,
            Connection = conn,
            ConnectionId = conn.Id,
            Dataset = req.Dataset,           // bare table/view name (SQLite has no schema)
            Visualizations = req.Visualizations
        };
        var result = svc.Compile(spec, schema);
        return Results.Ok(new
        {
            dashboardJson = RdashOutput.ToJson(result.Document),
            warnings = result.Warnings
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[AI Generate] {req?.Dataset}: {ex.Message}");
        return Results.Problem($"Could not generate a dashboard for '{req?.Dataset}': {ex.Message}");
    }
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status400BadRequest)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// POST /auth/login — validate the configured demo credentials and return a JWT.
// This is the only endpoint that allows anonymous access.
app.MapPost("/auth/login", (LoginRequest req, IConfiguration config) =>
{
    var expectedUser = config["Auth:Username"] ?? "admin";
    var expectedPass = config["Auth:Password"] ?? "demo2026";

    if (req is null ||
        !string.Equals(req.Username?.Trim(), expectedUser, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(req.Password, expectedPass, StringComparison.Ordinal))
    {
        return Results.Json(new { message = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var key = config["Auth:JwtKey"]!;
    var issuer = config["Auth:Issuer"] ?? "RevealNorthwindDemo";
    var audience = config["Auth:Audience"] ?? "RevealNorthwindDemo";
    var hours = int.TryParse(config["Auth:ExpiryHours"], out var h) ? h : 12;
    var expires = DateTime.UtcNow.AddHours(hours);

    var creds = new SigningCredentials(
        new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
        SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: issuer,
        audience: audience,
        claims: new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, expectedUser),
            new Claim(ClaimTypes.Name, expectedUser),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        },
        expires: expires,
        signingCredentials: creds);

    var jwt = new JwtSecurityTokenHandler().WriteToken(token);
    return Results.Ok(new { token = jwt, expiresAt = expires, username = expectedUser });
}).AllowAnonymous()
  .Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status401Unauthorized);

// ---------------------------------------------------------------------------
// Dynamic AI catalog: which tables/views of the active source the AI can use.
// Selection persists at Data/{sourceId}/ai-selection.json; PUT rewrites the
// Restricted catalog.json (picked up without restart — the file provider
// re-reads per call) and (re)generates the metadata store for the source.
// The client polls GET /api/reveal/ai/metadata/status for progress.
// ---------------------------------------------------------------------------

// GET /ai/catalog — available objects + current AI selection for the active source.
app.MapGet("/ai/catalog", (HttpContext http) =>
{
    try
    {
        var src = ResolveSource(http);
        var available = AiCatalogService.ListObjects(src.SqlitePath)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selected = aiCatalog.GetSelection(src.SourceId);
        return Results.Ok(new { sourceId = src.SourceId, available, selected });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error reading AI catalog: {ex.Message}");
        return Results.Problem($"Error reading AI catalog: {ex.Message}");
    }
}).Produces(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// PUT /ai/catalog — set the AI selection for the active source and regenerate metadata.
app.MapPut("/ai/catalog", async (AiCatalogUpdateRequest req, HttpContext http,
    Reveal.Sdk.AI.AspNetCore.Services.IMetadataService metadataService,
    Reveal.Sdk.AI.Metadata.IMetadataManager metadataManager,
    SuggestedQuestionsService questions) =>
{
    try
    {
        var src = sourceRegistry.Resolve(
            !string.IsNullOrWhiteSpace(req?.SourceId) ? req!.SourceId
            : http.Request.Headers["X-DataSource"].FirstOrDefault());

        if (metadataService.IsGenerationInProgress)
            return Results.Conflict(new { message = "Metadata generation is already running — try again shortly." });

        var validated = aiCatalog.SetSelection(src.SourceId, req?.Tables ?? new List<string>());

        // CRITICAL: the MetadataManager caches the catalog's datasource LIST in
        // memory at startup — chat resolves datasource ids against that cache, so
        // a source added/removed at runtime is invisible ("Datasource with ID 'x'
        // not found") until the manager reloads the rewritten catalog.json.
        await metadataManager.Reload();

        if (validated.Count == 0)
        {
            _ = Task.Run(async () =>
            {
                try { await metadataService.RemoveMetadataAsync(src.SourceId, null); }
                catch (Exception ex) { Console.WriteLine($"[AI Catalog] metadata removal for '{src.SourceId}' FAILED: {ex}"); }
            });
        }
        else
        {
            var whitelist = validated
                .Select(t => new Reveal.Sdk.AI.Metadata.MetadataWhitelistItem
                {
                    Database = Path.GetFileNameWithoutExtension(src.SqlitePath),
                    Table = t
                })
                .ToList();
            // Fire-and-forget: regeneration removes-then-generates internally; the
            // client polls /api/reveal/ai/metadata/status until Completed. Faults are
            // logged — a bare discarded Task would swallow them silently.
            _ = Task.Run(async () =>
            {
                try
                {
                    await metadataService.RegenerateMetadataAsync(src.SourceId, null, whitelist, CancellationToken.None);
                    // Re-apply user-authored catalog metadata (descriptions/aliases)
                    // to the freshly generated per-table files.
                    await metadataManager.Reload();
                }
                catch (Exception ex) { Console.WriteLine($"[AI Catalog] metadata regeneration for '{src.SourceId}' FAILED: {ex}"); }
            });
        }

        // The selection changed, so the cached starter questions are stale.
        questions.Invalidate(src.SourceId);
        _ = Task.Run(async () =>
        {
            try { await questions.RegenerateAsync(src.SourceId, CancellationToken.None); }
            catch (Exception ex) { Console.WriteLine($"[Suggestions] regeneration for '{src.SourceId}' failed: {ex.Message}"); }
        });

        return Results.Accepted("/api/reveal/ai/metadata/status", new { sourceId = src.SourceId, selected = validated });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error updating AI catalog: {ex.Message}");
        return Results.Problem($"Error updating AI catalog: {ex.Message}");
    }
}).Produces(StatusCodes.Status202Accepted)
  .Produces(StatusCodes.Status409Conflict)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// ---------------------------------------------------------------------------
// Custom AI metadata (#schema-reference): user-authored table descriptions and
// per-field alias/description. Stored per source at Data/{sourceId}/ai-metadata.json
// (so it SURVIVES every regeneration), merged into catalog.json on rebuild, and
// the metadata store is regenerated so the AI picks it up.
// ---------------------------------------------------------------------------

// GET /ai/metadata-overrides/{table} — current metadata for one table (active source).
app.MapGet("/ai/metadata-overrides/{table}", (string table, HttpContext http) =>
{
    try
    {
        var src = ResolveSource(http);
        var all = aiCatalog.GetMetadataOverrides(src.SourceId);
        all.TryGetValue(table, out var meta);
        var columns = AiCatalogService.DiscoverColumns(src.SqlitePath, new[] { table })
            .TryGetValue(table, out var cols) ? cols : new List<string>();
        return Results.Ok(new
        {
            sourceId = src.SourceId,
            table,
            columns,
            description = meta?.Description,
            fields = meta?.Fields ?? new Dictionary<string, FieldMetadata>(StringComparer.OrdinalIgnoreCase)
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error reading metadata overrides: {ex.Message}");
        return Results.Problem("Error reading metadata overrides.");
    }
}).Produces(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// PUT /ai/metadata-overrides/{table} — save the metadata, rebuild the catalog,
// reload the manager, and regenerate the source's metadata store.
app.MapPut("/ai/metadata-overrides/{table}", async (string table, TableMetadata body, HttpContext http,
    Reveal.Sdk.AI.AspNetCore.Services.IMetadataService metadataService,
    Reveal.Sdk.AI.Metadata.IMetadataManager metadataManager,
    SuggestedQuestionsService questions) =>
{
    try
    {
        var src = ResolveSource(http);
        if (!AiCatalogService.ListObjects(src.SqlitePath).Contains(table))
            return Results.NotFound(new { message = $"Table '{table}' was not found in '{src.SourceId}'." });

        if (metadataService.IsGenerationInProgress)
            return Results.Conflict(new { message = "Metadata generation is already running — try again shortly." });

        aiCatalog.SetTableMetadata(src.SourceId, table, body ?? new TableMetadata());
        await metadataManager.Reload();

        // Regenerate only when the table is part of the AI selection — otherwise the
        // metadata is stored and will apply when the table is enabled for the AI.
        var selection = aiCatalog.GetSelection(src.SourceId);
        var regenerating = selection.Contains(table, StringComparer.OrdinalIgnoreCase);
        if (regenerating)
        {
            var whitelist = selection
                .Select(t => new Reveal.Sdk.AI.Metadata.MetadataWhitelistItem
                {
                    Database = Path.GetFileNameWithoutExtension(src.SqlitePath),
                    Table = t
                })
                .ToList();
            _ = Task.Run(async () =>
            {
                try
                {
                    await metadataService.RegenerateMetadataAsync(src.SourceId, null, whitelist, CancellationToken.None);
                    // Regeneration writes fresh per-table files WITHOUT the catalog
                    // overrides; a reload re-applies them to the files (the chat read
                    // path also merges them in-memory, this keeps disk consistent).
                    await metadataManager.Reload();
                }
                catch (Exception ex) { Console.WriteLine($"[AI Metadata] regeneration for '{src.SourceId}' FAILED: {ex}"); }
            });
            questions.Invalidate(src.SourceId);
        }

        return Results.Accepted("/api/reveal/ai/metadata/status", new { sourceId = src.SourceId, table, regenerating });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error saving metadata overrides: {ex.Message}");
        return Results.Problem("Error saving metadata overrides.");
    }
}).Produces(StatusCodes.Status202Accepted)
  .Produces(StatusCodes.Status404NotFound)
  .Produces(StatusCodes.Status409Conflict)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// GET /ai/suggestions — per-source starter questions (LLM-generated, cached).
app.MapGet("/ai/suggestions", async (HttpContext http, SuggestedQuestionsService questions, CancellationToken ct) =>
{
    try
    {
        var src = ResolveSource(http);
        var result = await questions.GetAsync(src.SourceId, ct);
        return Results.Ok(new
        {
            sourceId = src.SourceId,
            questions = result.Questions,
            generatedAt = result.GeneratedAt,
            source = result.Source
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error getting suggestions: {ex.Message}");
        return Results.Problem($"Error getting suggestions: {ex.Message}");
    }
}).Produces(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// ---------------------------------------------------------------------------
// Share links (#9): POST /share creates a GUID for a dashboard; GET /share/{guid}
// (anonymous) exchanges it for a short-lived share JWT the public viewer uses for
// Reveal data calls. The token carries scope=share + sourceId, and a middleware
// below restricts share principals to read-style traffic.
// ---------------------------------------------------------------------------

// POST /share { dashboardName } — create a share link for a dashboard in the active source.
app.MapPost("/share", (ShareRequest req, HttpContext http, ShareService shares) =>
{
    if (string.IsNullOrWhiteSpace(req?.DashboardName))
        return Results.BadRequest(new { message = "A dashboardName is required." });

    var src = ResolveSource(http);
    var rdash = Path.Combine(src.DashboardsDir, req.DashboardName + ".rdash");
    if (!File.Exists(rdash))
        return Results.NotFound(new { message = $"Dashboard '{req.DashboardName}' was not found." });

    var id = shares.Create(src.SourceId, req.DashboardName);
    return Results.Ok(new { shareId = id, url = $"/share/{id}" });
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status400BadRequest)
  .Produces(StatusCodes.Status404NotFound);

// GET /share/{guid} — anonymous: resolve the link and mint a share JWT.
app.MapGet("/share/{guid:guid}", (Guid guid, ShareService shares, SourceRegistry reg, IConfiguration config) =>
{
    var entry = shares.Get(guid);
    if (entry is null) return Results.NotFound(new { message = "This share link is invalid or has been revoked." });

    var src = reg.Find(entry.SourceId);
    var rdash = src is null ? null : Path.Combine(src.DashboardsDir, entry.DashboardName + ".rdash");
    if (rdash is null || !File.Exists(rdash))
        return Results.NotFound(new { message = "The shared dashboard no longer exists." });

    // Share JWT: same key/issuer/audience as /auth/login so the existing bearer
    // validation accepts it; scope=share is enforced by the middleware below, and
    // the sourceId claim lets UserContextProvider resolve the right database.
    var key = config["Auth:JwtKey"]!;
    var hours = int.TryParse(config["Auth:ShareTokenHours"], out var h) ? h : 2;
    var expires = DateTime.UtcNow.AddHours(hours);
    var creds = new SigningCredentials(
        new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
        SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        issuer: config["Auth:Issuer"] ?? "RevealNorthwindDemo",
        audience: config["Auth:Audience"] ?? "RevealNorthwindDemo",
        claims: new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, $"share:{guid}"),
            new Claim(ClaimTypes.Name, "share-viewer"),
            new Claim("scope", "share"),
            new Claim("sourceId", entry.SourceId),
            new Claim("dashboardId", entry.DashboardName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        },
        expires: expires,
        signingCredentials: creds);

    return Results.Ok(new
    {
        dashboardName = entry.DashboardName,
        sourceId = entry.SourceId,
        shareToken = new JwtSecurityTokenHandler().WriteToken(token),
        expiresAt = expires
    });
}).AllowAnonymous()
  .Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound);

// DELETE /share/{guid} — revoke a share link (authenticated).
app.MapDelete("/share/{guid:guid}", (Guid guid, ShareService shares) =>
    shares.Remove(guid) ? Results.Ok() : Results.NotFound())
  .Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound);

// GET /share/{guid}/analysis — anonymous: the per-viz breakdown for the shared dashboard.
app.MapGet("/share/{guid:guid}/analysis", async (Guid guid, ShareService shares, DashboardAnalyzer analyzer, CancellationToken ct) =>
{
    var entry = shares.Get(guid);
    if (entry is null) return Results.NotFound(new { message = "This share link is invalid or has been revoked." });
    try
    {
        return Results.Ok(await analyzer.AnalyzeAsync(entry.SourceId, entry.DashboardName, ct));
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound(new { message = "The shared dashboard no longer exists." });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Analysis] {entry.DashboardName}: {ex.Message}");
        return Results.Problem("Could not analyze the dashboard.");
    }
}).AllowAnonymous()
  .Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// GET /dashboards/{name}/analysis — same breakdown for authenticated users.
app.MapGet("/dashboards/{name}/analysis", async (string name, HttpContext http, DashboardAnalyzer analyzer, CancellationToken ct) =>
{
    var src = ResolveSource(http);
    try
    {
        return Results.Ok(await analyzer.AnalyzeAsync(src.SourceId, name, ct));
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Analysis] {name}: {ex.Message}");
        return Results.Problem("Could not analyze the dashboard.");
    }
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// Share-scope hardening: a share JWT satisfies the global auth policy, so restrict
// share principals to read-style traffic — GET anywhere, plus the Reveal engine's
// own POSTs under "/dashboard/..." (widget data etc.; note "/dashboards" app routes
// are a DIFFERENT segment and stay blocked). Documented demo trade-off: a share
// token can still read other dashboards within the same source.
app.Use(async (context, next) =>
{
    if (context.User.HasClaim("scope", "share")
        && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method)
        && !HttpMethods.IsOptions(context.Request.Method)
        && !context.Request.Path.StartsWithSegments("/dashboard"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.MapControllers();

app.Run();

// ---------------------------------------------------------------------------
// Helper: build a RevealAI.Engine ConnectionConfig for a source's SQLite file.
// The connection Id IS the sourceId, so the compiled .rdash embeds a datasource
// that DataSourceProvider resolves straight back to the right .sqlite file
// (rule 1 of its ResolvePath). No credentials are involved.
// ---------------------------------------------------------------------------
static ConnectionConfig BuildAiConnection(string sourceId, string databasePath) => new()
{
    Id = sourceId,
    Title = $"{sourceId} (SQLite)",
    Type = ConnectionType.Sqlite,
    Database = databasePath
};

// ---------------------------------------------------------------------------
// Helper: drop the cached {name}.analysis.json next to a dashboard file so a
// save/delete invalidates the share-viewer breakdown immediately.
// ---------------------------------------------------------------------------
static void DeleteAnalysisCache(string rdashPath)
{
    try
    {
        var cache = Path.ChangeExtension(rdashPath, ".analysis.json");
        if (File.Exists(cache)) File.Delete(cache);
    }
    catch { /* best-effort */ }
}

// ---------------------------------------------------------------------------
// Helper: build a Microsoft.Data.Sqlite read-only connection string for a file.
// ---------------------------------------------------------------------------
static string SqliteConnString(string dbPath) =>
    new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ConnectionString;

// ---------------------------------------------------------------------------
// Helper: one-time migration from the legacy flat layout to folder-per-source.
//   Data/northwind.sqlite      -> Data/northwind/northwind.sqlite
//   Dashboards/*.rdash         -> Dashboards/northwind/*.rdash
// Also removes the stale AI metadata store written under the old catalog
// datasource id ("NorthwindSql"); the catalog id is the sourceId ("northwind")
// now, so the store regenerates under the new key on next startup.
// Idempotent: every step checks before acting; a second run is a no-op.
// ---------------------------------------------------------------------------
static void MigrateLegacyLayout(string contentRoot)
{
    try
    {
        var dataRoot = Path.Combine(contentRoot, "Data");
        var dashRoot = Path.Combine(contentRoot, "Dashboards");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(dashRoot);

        var legacyDb = Path.Combine(dataRoot, "northwind.sqlite");
        var northwindDir = Path.Combine(dataRoot, "northwind");
        if (File.Exists(legacyDb) && !File.Exists(Path.Combine(northwindDir, "northwind.sqlite")))
        {
            Directory.CreateDirectory(northwindDir);
            File.Move(legacyDb, Path.Combine(northwindDir, "northwind.sqlite"));
            Console.WriteLine("[Migrate] Data/northwind.sqlite -> Data/northwind/northwind.sqlite");
        }

        var flatDashboards = Directory.GetFiles(dashRoot, "*.rdash", SearchOption.TopDirectoryOnly);
        if (flatDashboards.Length > 0)
        {
            var northwindDash = Path.Combine(dashRoot, "northwind");
            Directory.CreateDirectory(northwindDash);
            foreach (var f in flatDashboards)
            {
                var target = Path.Combine(northwindDash, Path.GetFileName(f));
                if (!File.Exists(target)) File.Move(f, target);
            }
            Console.WriteLine($"[Migrate] moved {flatDashboards.Length} dashboard(s) -> Dashboards/northwind/");
        }

        // Stale metadata store from the old "NorthwindSql" catalog id.
        var metadataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "reveal", "ai", "metadata");
        if (Directory.Exists(metadataDir))
        {
            foreach (var f in Directory.GetFiles(metadataDir, "NorthwindSql_*"))
            {
                try { File.Delete(f); } catch { /* best-effort */ }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Migrate] Legacy layout migration failed: {ex.Message}");
    }
}

// (Catalog column discovery moved to AiCatalogService.DiscoverColumns.)

// ---------------------------------------------------------------------------
// Helper: persist a dashboard to disk as a valid .rdash file.
//
// The body is either:
//   * raw .rdash bytes (a ZIP, PK magic 0x50 0x4B) produced by the Reveal SDK's
//     serialize()/serializeWithNewName() — written to disk unchanged; or
//   * Reveal DOM JSON produced by the AI Assistant — parsed with
//     RdashDocument.LoadFromJson(...) and saved via RdashDocument.Save(), which
//     writes the correct on-disk .rdash format. (Naively zipping the JSON as
//     "dashboard.json" does NOT produce a loadable .rdash.)
// ---------------------------------------------------------------------------
static void SaveDashboardDocument(byte[] bytes, string filePath)
{
    // Already a ZIP/.rdash from the Reveal SDK — write through unchanged.
    if (bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B)
    {
        File.WriteAllBytes(filePath, bytes);
        return;
    }

    // Reveal DOM JSON (from the AI Assistant) — let the DOM produce a real .rdash.
    var json = System.Text.Encoding.UTF8.GetString(bytes);
    var document = RdashDocument.LoadFromJson(json);
    document.Save(filePath);
}

// Body shape for POST /auth/login
record LoginRequest(string? Username, string? Password);

// Body shapes for the AI visualization endpoints.
record RecommendRequest(string Dataset, string? Guidance);
record GenerateRequest(string Title, string Dataset, List<VisualizationSpec> Visualizations);

// Body shape for PUT /ai/catalog.
record AiCatalogUpdateRequest(string? SourceId, List<string> Tables);

// Body shape for POST /share.
record ShareRequest(string DashboardName);
