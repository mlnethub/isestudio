using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Api;
using OnToPilot.Application.Integration;
using OnToPilot.Authentication;
using OnToPilot.Authorization;
using OnToPilot.Configuration;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Startup;
using OnToPilot.Integration;
using OnToPilot.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Wire the source-generated JSON serializer context so every typed DTO
// the controllers return (FastApiError, OntologyResponse, ChangePreview,
// QueryResponse) hits System.Text.Json's compile-time path. The
// resolver chain keeps the default reflection-based resolver in place
// for the anonymous placeholder payloads the InternalOperationDispatcher
// emits until the Stage 2/3 services land; once those become typed DTOs
// they take the source-gen path and skip reflection entirely. See
// src/OnToPilot/Serialization/OnToPilotJsonContext.cs.
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(
        OnToPilotJsonContext.Default,
        new DefaultJsonTypeInfoResolver());
});

// Single in-process dispatcher + facade. Controllers depend only on the
// facade; the dispatcher is the implementation seam for swapping in
// per-operation delegates as Stage 2/3 services stabilise.
builder.Services.AddSingleton<IInternalOperationDispatcher, InternalOperationDispatcher>();
builder.Services.AddScoped<IIntegrationApiFacade, IntegrationApiFacade>();

builder.Services.Configure<OnToPilotOptions>(
    builder.Configuration.GetSection(OnToPilotOptions.SectionName));

// Wire the EF Core DbContext. Production uses PostgreSQL; tests flip the
// provider via configuration (see AuthTestWebApplicationFactory). The actual
// schema is owned by the InitialCompatibility migration; the application
// does not call EnsureCreated() at runtime. The provider choice is resolved
// lazily from the IServiceProvider so test factories that add configuration
// sources late in `ConfigureWebHost` are honored.
builder.Services.AddDbContext<OnToPilotDbContext>((sp, options) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var provider = config["OnToPilot:Persistence:Provider"] ?? "npgsql";
    if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var sqlite = config["OnToPilot:Persistence:SqliteConnection"]
            ?? "Data Source=:memory:";
        options.UseSqlite(sqlite);
    }
    else
    {
        var npgsql = config["OnToPilot:Persistence:ConnectionString"]
            ?? "Host=localhost;Port=5432;Database=ontopilot;Username=postgres;Password=postgres";
        options.UseNpgsql(npgsql);
    }
});

// Also register an IDbContextFactory<OnToPilotDbContext> so background
// services (the ExtractionOrchestrator, the InternalOperationDispatcher's
// "is extraction active" guard) can open a fresh DbContext per call without
// sharing the scoped HttpContext-bound tracker. Both registrations point at
// the same configuration so the production connection string and the
// contract-test sqlite file are honoured identically.
builder.Services.AddDbContextFactory<OnToPilotDbContext>((sp, options) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var provider = config["OnToPilot:Persistence:Provider"] ?? "npgsql";
    if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var sqlite = config["OnToPilot:Persistence:SqliteConnection"]
            ?? "Data Source=:memory:";
        options.UseSqlite(sqlite);
    }
    else
    {
        var npgsql = config["OnToPilot:Persistence:ConnectionString"]
            ?? "Host=localhost;Port=5432;Database=ontopilot;Username=postgres;Password=postgres";
        options.UseNpgsql(npgsql);
    }
});

// Singleton so the dispatcher's "find any active extraction" guard does
// not have to share state with the request-scoped DbContext; the store
// uses its own IDbContextFactory.
builder.Services.AddSingleton<ExtractionJobStore>();

// ---- Auth services ----
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordService, PasswordService>();
builder.Services.AddSingleton<KnowledgeSystemAccessService>();

// Bearer-token primitives (scoped to the request DbContext).
builder.Services.AddScoped<IKnowledgeApiTokenService, KnowledgeApiTokenService>();
builder.Services.AddScoped<IMcpTokenService, McpTokenService>();

// Startup recovery hosted services (scoped to a single DbContext per run).
builder.Services.AddScoped<BootstrapAdminService>();
builder.Services.AddScoped<StaleJobRecoveryService>();
builder.Services.AddScoped<LegacyBackfillService>();

builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName,
        _ => { })
    .AddScheme<AuthenticationSchemeOptions, ApiBearerAuthenticationHandler>(
        ApiBearerAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization();

// ASP.NET Core 10 ships the OpenAPI document at /openapi/v1.json when both
// the transformer services and the endpoint mapping are registered. The
// inventory test reads this URL, so the registration has to live next to
// the rest of the pipeline wiring.
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();

// Translate model-state validation failures into the FastAPI envelope
// ({ "detail": "..." }) instead of the default application/problem+json
// body that [ApiController] otherwise emits.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var detail = context.ModelState
            .Where(kv => kv.Value is { Errors.Count: > 0 })
            .Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value!.Errors.Select(e => e.ErrorMessage))}")
            .FirstOrDefault()
            ?? "Invalid request";
        return new BadRequestObjectResult(new { detail });
    };
});

var app = builder.Build();

// Global error envelope runs FIRST so every response shape (auth challenges,
// model validation, unhandled exceptions, 404 routes) ultimately carries
// {"detail": "..."} — the same shape the Python backend emits.
app.UseMiddleware<FastApiErrorMiddleware>();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapOpenApi();

// ---- Bootstrap recovery ----
// Empty installs MUST NOT auto-create a default admin user — the service
// refuses and exits with a documented non-zero code so the operator can
// provision the first user manually (via SSH / kubectl exec / etc.).
//
// For the SQLite provider (tests + dev) we EnsureCreated here so the
// bootstrap check has a table to query. For PostgreSQL we trust the
// deploy-time migrations to have applied the InitialCompatibility
// migration; the schema must exist before this process starts.
//
// Test environments opt out via the "Testing" environment so individual
// tests can seed users through `WebApplicationFactory.CreateDbContext()`
// without the bootstrap step refusing to start.
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
    if (db.Database.IsSqlite())
    {
        await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
    }

    var bootstrap = scope.ServiceProvider.GetRequiredService<BootstrapAdminService>();
    var outcome = await bootstrap.RunAsync(default).ConfigureAwait(false);
    if (outcome == BootstrapOutcome.BootstrapRequired)
    {
        Environment.ExitCode = bootstrap.ExitCode;
        // Don't call app.Run(); the host hasn't bound sockets yet, and
        // returning from top-level statements is the cleanest signal.
        return;
    }

    // Brief 接口 clause: every boot must also run stale-job/deployment
    // recovery (so extraction_active() doesn't keep reporting a busy KS
    // after a crash) and orphan-document backfill (so documents created
    // before the first KS existed are bound to one). Both services are
    // idempotent and re-entrant.
    await scope.ServiceProvider
        .GetRequiredService<StaleJobRecoveryService>()
        .RunAsync(default)
        .ConfigureAwait(false);
    await scope.ServiceProvider
        .GetRequiredService<LegacyBackfillService>()
        .RunAsync(default)
        .ConfigureAwait(false);
}

app.Run();

/// <summary>
/// Exposed as a partial class so test projects can derive
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// against <c>Program</c>.
/// </summary>
public partial class Program;
