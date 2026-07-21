using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
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

// ---- Build the Reveal AI metadata catalog (whitelisted tables/views) ----
// A single whitelist — SqlServer:CatalogObjects in appsettings — is the source of
// truth, and it is used TWO ways (see https://help.revealbi.io/ai/metadata-catalog/):
//   1) HERE: build a "Restricted" catalog.json that is handed to the Reveal AI SDK
//      below via UseMetadataCatalogFile. Restricted discovery means Reveal AI only
//      ever sees and queries the listed objects — everything else is invisible to it.
//   2) The /sql/objects endpoint filters the Connections page's tables/views list to
//      exactly this same set, so the UI and the AI agree on what is available.
// For each whitelisted object we look up its real columns so the catalog carries
// field metadata (which materially improves the AI's query/visualization quality).
var sqlOptions = builder.Configuration.GetSection("SqlServer").Get<SqlServerOptions>()
                 ?? new SqlServerOptions();
var sqlSchema = string.IsNullOrWhiteSpace(sqlOptions.Schema) ? "dbo" : sqlOptions.Schema;

// The whitelist (bare, unqualified names). Captured by the /sql endpoints below too.
var catalogObjects = (sqlOptions.CatalogObjects ?? Array.Empty<string>())
    .Where(n => !string.IsNullOrWhiteSpace(n))
    .Select(n => n.Trim())
    .ToArray();

var catalogColumns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
try
{
    catalogColumns = DiscoverColumns(sqlOptions, catalogObjects);
    Console.WriteLine($"[AI Catalog] Whitelisted {catalogObjects.Length} object(s): {string.Join(", ", catalogObjects)}");
}
catch (Exception ex)
{
    Console.WriteLine($"[AI Catalog] Could not read columns for catalog objects: {ex.Message}");
}

// Anchor to ContentRootPath (NOT Directory.GetCurrentDirectory()): on Azure App
// Service the process working directory is not the app's content root, so a relative
// path here would write/read different folders than UseMetadataCatalogFile resolves,
// leaving the AI engine with an empty catalog ("key 'NorthwindSql' not present").
var catalogDir = Path.Combine(builder.Environment.ContentRootPath, "Reveal", "Metadata");
Directory.CreateDirectory(catalogDir);
var catalogPath = Path.Combine(catalogDir, "catalog.json");
var catalog = new
{
    Datasources = new object[]
    {
        new
        {
            Id = "NorthwindSql",
            Provider = "SQLServer",
            Databases = new object[]
            {
                new
                {
                    Name = sqlOptions.Database,
                    DiscoveryMode = "Restricted",
                    Tables = catalogObjects.Select(v => new
                    {
                        Name = $"{sqlSchema}.{v}",
                        Fields = (catalogColumns.TryGetValue(v, out var cols) ? cols : new List<string>())
                            .Select(c => new { Name = c })
                            .ToArray()
                    }).ToArray()
                }
            }
        }
    }
};
File.WriteAllText(catalogPath,
    System.Text.Json.JsonSerializer.Serialize(catalog,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

// Case-insensitive lookup of the whitelist, shared by the /sql browsing endpoints.
// Null when no whitelist is configured (endpoints then fall back to showing everything).
var catalogFilter = catalogObjects.Length > 0
    ? new HashSet<string>(catalogObjects, StringComparer.OrdinalIgnoreCase)
    : null;
// ---------------------------------------------------------------------------

builder.Services.AddControllers().AddReveal(revealBuilder =>
{
    revealBuilder        
        .AddAuthenticationProvider<AuthenticationProvider>()
        .AddDataSourceProvider<DataSourceProvider>()
        .AddObjectFilter<DataSourceItemFilter>()
        .AddUserContextProvider<UserContextProvider>()
        .DataSources.RegisterMicrosoftSqlServer();

    revealBuilder
        .AddSettings(settings =>
        {
            settings.License = "eyJhbGciOiJQUzUxMiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3NzMyNDA0NzIsIm5iZiI6MTc3MzI0MDQ3MiwiaWQiOiJhMFdWTTAwMDAwNkp5clIyQVMiLCJwcm9kdWN0X2NvZGUiOiJBOCIsInByb2R1Y3RfdmVyc2lvbiI6IjcwIiwicHJvZHVjdF9wbGFuIjoicmV2ZWFsLXBybyIsInNlcnZpY2VfZW5kX2RhdGUiOiIyMDI3LTAzLTExVDAwOjAwOjAwLjAwMDAwMDBaIiwic2VydmljZV9sZXZlbCI6IlByaW9yaXR5In0.HrWe-xkY48l6euIT63lE9wKx7ye4KH0GRzBD3Bl9xfCP6hRNeR3yyxoiVI54zO81y3jyb9YMeSvh-8pjwCfL_0c-vNJA2vBRk-3gj-EjjSGljINkJlMDGQIklQOEtbn_8YEiSpNKlNTRHDAfqYikhGBYRs9HrKE4eZKEAVgJKBi0jv1Kp8ztVHlLvbmOddC3p-TwYH-QXiag4xhh4oiQydKbKaCXZx1CAsiwrUJ-DEz0k85U14YsKPouMyCy3vZxshvcodgIKxREa9OnQNY3qGGF19fhsLr8D2o43zSplKO5knAHJVUZVwEDgsF-CsvGwqrc3N6JKSEoB0PUQiycaQ";
            settings.LocalFileStoragePath = "Data";
        });
});

builder.Services.Configure<SqlServerOptions>(
    builder.Configuration.GetSection("SqlServer"));

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

// Serve Excel files from the Data folder at /data/{filename}.xlsx
var dataFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "Data");
if (!Directory.Exists(dataFolderPath))
    Directory.CreateDirectory(dataFolderPath);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataFolderPath),
    RequestPath = "/data",
    ServeUnknownFileTypes = false,
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
    }
});


app.MapGet("/dashboards/names", () =>
{
    try
    {
        string folderPath = Path.Combine(Directory.GetCurrentDirectory(), "Dashboards");
        var files = Directory.GetFiles(folderPath);
        Random rand = new();

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

app.MapGet("/dashboards/{name}/thumbnail", (string name) =>
{
    var path = "dashboards/" + name + ".rdash";
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

app.MapGet("dashboards/visualizations", () =>
{
    try
    {
        var allVisualizationChartInfos = new List<VisualizationChartInfo>();
        var dashboardFiles = Directory.GetFiles("Dashboards", "*.rdash");

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

app.MapGet("/data/files", () =>
{
    try
    {
        string folderPath = Path.Combine(Directory.GetCurrentDirectory(), "Data");
        
        if (!Directory.Exists(folderPath))
        {
            return Results.NotFound("Data folder not found.");
        }

        var files = Directory.GetFiles(folderPath);
        var fileNames = files.Select(file => Path.GetFileNameWithoutExtension(file)).ToList();

        return Results.Ok(fileNames);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error reading Data directory: {ex.Message}");
        return Results.Problem("An unexpected error occurred while processing the request.");
    }
}).Produces<IEnumerable<string>>(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

app.MapPost("/data/upload", async (IFormFile file) =>
{
    try
    {
        if (file == null || file.Length == 0)
        {
            return Results.BadRequest(new { message = "No file provided." });
        }

        var allowedExtensions = new[] { ".xlsx", ".xls" };
        var fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();

        if (!allowedExtensions.Contains(fileExtension))
        {
            return Results.BadRequest(new { message = "Only Excel files (.xlsx, .xls) are allowed." });
        }

        string folderPath = Path.Combine(Directory.GetCurrentDirectory(), "Data");
        
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        var fileName = Path.GetFileName(file.FileName);
        var filePath = Path.Combine(folderPath, fileName);

        if (File.Exists(filePath))
        {
            return Results.Conflict(new { message = $"File '{fileName}' already exists." });
        }

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        return Results.Ok(new { message = "File uploaded successfully.", fileName = fileName });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error uploading file: {ex.Message}");
        return Results.Problem("An error occurred while uploading the file.");
    }
}).Accepts<IFormFile>("multipart/form-data")
  .Produces<object>(StatusCodes.Status200OK)
  .Produces<object>(StatusCodes.Status400BadRequest)
  .Produces<object>(StatusCodes.Status409Conflict)
  .ProducesProblem(StatusCodes.Status500InternalServerError)
  .DisableAntiforgery();


// Check if a dashboard name already exists
app.MapGet("/isduplicatename/{name}", (string name) =>
{
    var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "Dashboards");
    return Results.Ok(File.Exists(Path.Combine(folderPath, $"{name}.rdash")));
});

// GET /dashboards/{name} — return the raw .rdash file bytes.
// Used by the Visualization Catalog, which fetches a source dashboard's bytes and
// parses them with RdashDocument.load(blob) to import individual visualizations
// into a new composite dashboard. Without this, the route only had POST/PUT/DELETE
// handlers, so a GET matched the template but no method → 405 Method Not Allowed.
// Literal segments ("names", "visualizations") outrank the {name} parameter in
// routing, so those GET endpoints above still take precedence.
app.MapGet("/dashboards/{name}", (string name) =>
{
    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "Dashboards", $"{name}.rdash");
    if (!File.Exists(filePath)) return Results.NotFound();
    return Results.File(File.ReadAllBytes(filePath), "application/octet-stream", $"{name}.rdash");
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound);

// POST /dashboards/{name} — create new dashboard
// Accepts either raw rdash bytes (ZIP) from the Reveal SDK or DOM JSON from the
// AI Assistant. DOM JSON is parsed by the Reveal DOM and saved as a real .rdash.
app.MapPost("/dashboards/{name}", async (HttpRequest request, string name) =>
{
    var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "Dashboards", $"{name}.rdash");
    try
    {
        SaveDashboardDocument(ms.ToArray(), filePath);
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
app.MapPut("/dashboards/{name}", async (HttpRequest request, string name) =>
{
    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "Dashboards", $"{name}.rdash");
    if (!File.Exists(filePath)) return Results.NotFound();
    var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    try
    {
        SaveDashboardDocument(ms.ToArray(), filePath);
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

// DELETE /data/files/{name} — delete an Excel file from the Data folder
app.MapDelete("/data/files/{name}", (string name) =>
{
    var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "Data");
    var xlsxPath = Path.Combine(folderPath, $"{name}.xlsx");
    var xlsPath  = Path.Combine(folderPath, $"{name}.xls");
    var filePath = File.Exists(xlsxPath) ? xlsxPath : File.Exists(xlsPath) ? xlsPath : null;
    if (filePath is null) return Results.NotFound();
    File.Delete(filePath);
    return Results.Ok();
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound);

// DELETE /dashboards/{name} — remove a dashboard file
app.MapDelete("/dashboards/{name}", (string name) =>
{
    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "Dashboards", $"{name}.rdash");
    if (!File.Exists(filePath)) return Results.NotFound();
    File.Delete(filePath);
    return Results.Ok();
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound);

// ---------------------------------------------------------------------------
// SQL Server browsing endpoints (used by the client Connections page)
// Connection details come from the "SqlServer" section of appsettings.json,
// bound to SqlServerOptions and injected via IOptions<SqlServerOptions>.
// ---------------------------------------------------------------------------

// GET /sql/connection — connection metadata (no credentials) for the tree header
app.MapGet("/sql/connection", (IOptions<SqlServerOptions> options) =>
{
    var o = options.Value;
    return Results.Ok(new
    {
        host = o.Host,
        database = o.Database,
        schema = string.IsNullOrWhiteSpace(o.Schema) ? "dbo" : o.Schema
    });
});

// GET /sql/objects — list all tables and views in the configured schema
app.MapGet("/sql/objects", async (IOptions<SqlServerOptions> options) =>
{
    var o = options.Value;
    var schema = string.IsNullOrWhiteSpace(o.Schema) ? "dbo" : o.Schema;
    try
    {
        await using var conn = new SqlConnection(BuildSqlConnectionString(o));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT TABLE_NAME, TABLE_TYPE
                            FROM INFORMATION_SCHEMA.TABLES
                            WHERE TABLE_SCHEMA = @schema
                            ORDER BY TABLE_NAME";
        cmd.Parameters.AddWithValue("@schema", schema);
        await using var reader = await cmd.ExecuteReaderAsync();

        var objects = new List<object>();
        while (await reader.ReadAsync())
        {
            var objectName = reader.GetString(0);
            // Whitelist: when SqlServer:CatalogObjects is set, the Connections page only
            // shows those objects — the same set the Reveal AI metadata catalog is built from.
            if (catalogFilter != null && !catalogFilter.Contains(objectName)) continue;
            objects.Add(new
            {
                name = objectName,
                type = reader.GetString(1) == "VIEW" ? "view" : "table"
            });
        }
        return Results.Ok(objects);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error listing SQL objects: {ex.Message}");
        return Results.Problem($"Error listing SQL objects: {ex.Message}");
    }
}).Produces(StatusCodes.Status200OK)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// GET /sql/data/{name}?top=N — return rows + column metadata for a table/view
app.MapGet("/sql/data/{name}", async (string name, int? top, IOptions<SqlServerOptions> options) =>
{
    var o = options.Value;
    var schema = string.IsNullOrWhiteSpace(o.Schema) ? "dbo" : o.Schema;
    try
    {
        await using var conn = new SqlConnection(BuildSqlConnectionString(o));
        await conn.OpenAsync();

        // Verify the object exists in the schema. This also guards against SQL
        // injection: only a name that matches a real catalog entry is queried.
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = @"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
                                  WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name";
            check.Parameters.AddWithValue("@schema", schema);
            check.Parameters.AddWithValue("@name", name);
            var exists = Convert.ToInt32(await check.ExecuteScalarAsync() ?? 0);
            if (exists == 0)
                return Results.NotFound(new { message = $"Object '{name}' not found in schema '{schema}'." });
        }

        var limit = top is > 0 and <= 100000 ? top.Value : 1000;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT TOP ({limit}) * FROM [{schema}].[{name}]";
        await using var reader = await cmd.ExecuteReaderAsync();

        var columns = new List<object>();
        for (int i = 0; i < reader.FieldCount; i++)
            columns.Add(new { name = reader.GetName(i), type = reader.GetFieldType(i).Name });

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return Results.Ok(new { name, schema, columns, rowCount = rows.Count, rows });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error reading '{name}': {ex.Message}");
        return Results.Problem($"Error reading '{name}': {ex.Message}");
    }
}).Produces(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status404NotFound)
  .ProducesProblem(StatusCodes.Status500InternalServerError);

// GET /sql/rowcounts — { name: rowCount } for every table/view in the schema.
// Tables use fast catalog statistics (approximate); views need COUNT(*) (exact),
// each guarded by a short timeout so one slow view can't stall the rest. Best-effort:
// objects whose count can't be obtained are simply omitted. Called by the tree to
// populate per-object chips AFTER it renders, so the list never waits on this.
app.MapGet("/sql/rowcounts", async (IOptions<SqlServerOptions> options, CancellationToken ct) =>
{
    var o = options.Value;
    var schema = string.IsNullOrWhiteSpace(o.Schema) ? "dbo" : o.Schema;
    var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    try
    {
        await using var conn = new SqlConnection(BuildSqlConnectionString(o));
        await conn.OpenAsync(ct);

        // Tables: approximate row counts from partition stats (instant, no scan).
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT t.name, SUM(p.rows)
                                FROM sys.tables t
                                JOIN sys.schemas s ON s.schema_id = t.schema_id
                                JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
                                WHERE s.name = @schema
                                GROUP BY t.name";
            cmd.Parameters.AddWithValue("@schema", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var tableName = r.GetString(0);
                if (catalogFilter != null && !catalogFilter.Contains(tableName)) continue;
                if (!await r.IsDBNullAsync(1, ct)) counts[tableName] = Convert.ToInt64(r.GetValue(1));
            }
        }

        // Views: no stored stats — COUNT_BIG(*) each, guarded with a short timeout.
        var views = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_SCHEMA = @schema";
            cmd.Parameters.AddWithValue("@schema", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var viewName = r.GetString(0);
                // Only compute counts for whitelisted views (matches /sql/objects).
                if (catalogFilter != null && !catalogFilter.Contains(viewName)) continue;
                views.Add(viewName);
            }
        }
        foreach (var v in views)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = 8;
                cmd.CommandText = $"SELECT COUNT_BIG(*) FROM [{schema}].[{v}]";   // v is a real catalog name, not user input
                var n = await cmd.ExecuteScalarAsync(ct);
                if (n is not null && n != DBNull.Value) counts[v] = Convert.ToInt64(n);
            }
            catch { /* skip views that time out or error — chips are best-effort */ }
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
// "Generate Visualizations" flow. The connection is built from the same
// "SqlServer" config the rest of the app uses; credentials here are for
// generation-time introspection only (the rendered .rdash resolves credentials
// at view time via DataSourceProvider/AuthenticationProvider).
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
           IOptions<SqlServerOptions> opt, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req?.Dataset))
        return Results.BadRequest(new { message = "A dataset (table or view name) is required." });
    try
    {
        var conn = BuildAiConnection(opt.Value);
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
           IOptions<SqlServerOptions> opt, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req?.Dataset))
        return Results.BadRequest(new { message = "A dataset (table or view name) is required." });
    if (req.Visualizations is null || req.Visualizations.Count == 0)
        return Results.BadRequest(new { message = "Select at least one visualization to generate." });
    try
    {
        var conn = BuildAiConnection(opt.Value);
        var schema = await introspect.IntrospectAsync(conn, req.Dataset, 50, ct);
        var spec = new DashboardSpec
        {
            Title = string.IsNullOrWhiteSpace(req.Title) ? req.Dataset : req.Title,
            Connection = conn,
            ConnectionId = conn.Id,
            Dataset = req.Dataset,           // bare table name; conn.Schema supplies the schema
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

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// ---------------------------------------------------------------------------
// Helper: build a RevealAI.Engine ConnectionConfig from the configured SqlServer
// options. Type is forced to SqlServer (the Microsoft SQL connector) so the
// compiled .rdash uses the SAME data-source shape the existing dashboards use
// (RVSqlServerDataSource id "sqlServer"), which the client's onDataSourcesRequested
// and the server's DataSourceProvider/AuthenticationProvider already resolve.
// ---------------------------------------------------------------------------
static ConnectionConfig BuildAiConnection(SqlServerOptions o) => new()
{
    Id = "sqlServer",
    Title = "SQL Server Data Source",
    Type = ConnectionType.SqlServer,
    Host = o.Host,
    Database = o.Database,
    Username = o.Username,
    Password = o.Password,
    Schema = string.IsNullOrWhiteSpace(o.Schema) ? "dbo" : o.Schema
};

// ---------------------------------------------------------------------------
// Helper: build a SQL Server connection string from the configured options.
// Encrypt + TrustServerCertificate are set for Azure SQL compatibility.
// ---------------------------------------------------------------------------
static string BuildSqlConnectionString(SqlServerOptions o)
{
    return new SqlConnectionStringBuilder
    {
        DataSource = o.Host,
        InitialCatalog = o.Database,
        UserID = o.Username,
        Password = o.Password,
        TrustServerCertificate = o.TrustServerCertificate,
        Encrypt = true
    }.ConnectionString;
}

// ---------------------------------------------------------------------------
// Helper: read the column names for a specific set of tables/views (the catalog
// whitelist) from the configured database. Used at startup to build the AI
// metadata catalog with field metadata for exactly the whitelisted objects.
// Works for both tables and views — INFORMATION_SCHEMA.COLUMNS covers both.
// The dictionary is pre-seeded with the requested names (case-insensitive), so
// only those objects' columns are retained even though the query reads the schema.
// ---------------------------------------------------------------------------
static Dictionary<string, List<string>> DiscoverColumns(SqlServerOptions o, string[] names)
{
    var columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    if (names is null || names.Length == 0) return columns;

    var schema = string.IsNullOrWhiteSpace(o.Schema) ? "dbo" : o.Schema;
    foreach (var n in names) columns[n] = new List<string>();

    using var conn = new SqlConnection(BuildSqlConnectionString(o));
    conn.Open();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_SCHEMA = @schema
                        ORDER BY TABLE_NAME, ORDINAL_POSITION";
    cmd.Parameters.AddWithValue("@schema", schema);
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        var table = r.GetString(0);
        if (columns.TryGetValue(table, out var list)) list.Add(r.GetString(1));
    }

    return columns;
}

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
