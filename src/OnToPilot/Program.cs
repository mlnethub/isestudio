using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using Npgsql;
using OnToPilot.Api;
using OnToPilot.Application.Integration;
using OnToPilot.Authentication;
using OnToPilot.Authorization;
using OnToPilot.Configuration;
using OnToPilot.Conflicts;
using OnToPilot.Documents;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Infrastructure.Startup;
using OnToPilot.Integration;
using OnToPilot.Knowledge;
using OnToPilot.Mcp;
using OnToPilot.Observability;
using OnToPilot.Ontology;
using OnToPilot.Parsing;
using OnToPilot.Providers;
using OnToPilot.Serialization;
using OnToPilot.Storage;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---- Migration entry point ----
// When invoked with `--migrate`, apply EF Core migrations against the
// configured persistence provider and exit. The docker-compose `migrate`
// init service uses this exact image so the migration logic and the
// DbContext stay co-located — no `dotnet-ef` global tool is needed in
// the runtime image (the migration C# files are compiled into the
// assembly, and EF Core ships the runtime to apply them).
//
// Compose wires `backend.depends_on.migrate` with
// `service_completed_successfully`, so the API never starts against a
// schema that's behind the migrations baked into the image. Idempotent:
// re-running is safe because EF Core consults `__EFMigrationsHistory`.
if (args.Contains("--migrate"))
{
    var migrateConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    var provider = migrateConfig["OnToPilot:Persistence:Provider"] ?? "npgsql";
    var optionsBuilder = new DbContextOptionsBuilder<OnToPilotDbContext>();
    if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var sqlite = migrateConfig["OnToPilot:Persistence:SqliteConnection"]
            ?? "Data Source=:memory:";
        optionsBuilder.UseSqlite(sqlite);
    }
    else
    {
        var npgsql = migrateConfig["OnToPilot:Persistence:ConnectionString"]
            ?? throw new InvalidOperationException(
                "OnToPilot:Persistence:ConnectionString is required for the --migrate entry point.");
        optionsBuilder.UseNpgsql(npgsql);
    }

    await using var migrateDb = new OnToPilotDbContext(optionsBuilder.Options);
    Console.WriteLine($"[migrate] Applying EF Core migrations against provider '{provider}'...");
    await migrateDb.Database.MigrateAsync().ConfigureAwait(false);
    Console.WriteLine("[migrate] Done.");
    return;
}

// ---- Seed-admin entry point ----
// When invoked with `--seed-admin`, insert the first administrator user
// into the database and exit. Same pattern as `--migrate`: an init-style
// container (or a one-off `docker compose run`) uses the backend image
// with a different ENTRYPOINT so the seeding logic and the password
// service stay co-located with the application.
//
// Inputs come from environment variables so compose can wire them
// through `environment:` without touching CLI argv parsing:
//   SEED_ADMIN_USERNAME      required
//   SEED_ADMIN_PASSWORD      required; validated via PasswordService
//                            with bootstrap=true (rejects published example
//                            passwords, requires ≥12 chars, ≤72 UTF-8 bytes)
//   SEED_ADMIN_DISPLAY_NAME  optional; falls back to the username
//
// Idempotency contract:
//   * No users at all          → insert, exit 0
//   * Username exists & admin  → already done, exit 0 (re-runs are no-ops)
//   * Username exists & not admin → refuse with exit 1 (operator typo)
//   * A different admin already exists → refuse with exit 1 (don't
//     silently duplicate; operator must drop the existing row in SQL if
//     they really want to reseed with a new username)
//
// Invoke via docker compose after migrations have run:
//   docker compose --profile bootstrap run --rm seed-admin
if (args.Contains("--seed-admin"))
{
    var seedConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    var username = seedConfig["SEED_ADMIN_USERNAME"];
    var password = seedConfig["SEED_ADMIN_PASSWORD"];
    var displayName = seedConfig["SEED_ADMIN_DISPLAY_NAME"];

    if (string.IsNullOrWhiteSpace(username))
    {
        throw new InvalidOperationException(
            "SEED_ADMIN_USERNAME is required for --seed-admin.");
    }
    if (string.IsNullOrEmpty(password))
    {
        throw new InvalidOperationException(
            "SEED_ADMIN_PASSWORD is required for --seed-admin.");
    }

    // Reuse the same provider/connection-string resolver as --migrate so
    // SQLite (tests, dev) and PostgreSQL (compose, prod) are honored
    // identically.
    var seedProvider = seedConfig["OnToPilot:Persistence:Provider"] ?? "npgsql";
    var seedOptionsBuilder = new DbContextOptionsBuilder<OnToPilotDbContext>();
    if (string.Equals(seedProvider, "sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var sqlite = seedConfig["OnToPilot:Persistence:SqliteConnection"]
            ?? "Data Source=:memory:";
        seedOptionsBuilder.UseSqlite(sqlite);
    }
    else
    {
        var npgsql = seedConfig["OnToPilot:Persistence:ConnectionString"]
            ?? throw new InvalidOperationException(
                "OnToPilot:Persistence:ConnectionString is required for --seed-admin.");
        seedOptionsBuilder.UseNpgsql(npgsql);
    }

    await using var seedDb = new OnToPilotDbContext(seedOptionsBuilder.Options);

    // Refuse if a DIFFERENT admin already exists — silently inserting a
    // second admin would make the bootstrap check pass against the wrong
    // account. The operator must drop the existing row in SQL if they
    // really want to reseed with a new username.
    var existingAdmin = await seedDb.Users
        .Where(u => u.IsAdmin)
        .Select(u => u.Username)
        .FirstOrDefaultAsync()
        .ConfigureAwait(false);
    if (existingAdmin is not null && !string.Equals(existingAdmin, username, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Refused to seed: an admin user '{existingAdmin}' already exists. " +
            "Drop the existing admin row in SQL if you really want to reseed with a different username.");
    }

    // Idempotent: if THIS username already exists and is already an admin,
    // we treat the call as a no-op success. The bootstrap check downstream
    // then sees a populated users table and proceeds.
    var existingSame = await seedDb.Users
        .FirstOrDefaultAsync(u => u.Username == username)
        .ConfigureAwait(false);
    if (existingSame is not null)
    {
        if (!existingSame.IsAdmin)
        {
            throw new InvalidOperationException(
                $"Refused to seed: user '{username}' exists but is not an admin. " +
                "Update the IsAdmin flag in SQL, or pick a different username.");
        }
        Console.WriteLine($"[seed-admin] Admin '{username}' already exists; nothing to do.");
        return;
    }

    // Validate the password with bootstrap=true so published-example
    // passwords (admin / changeme / password / …) are rejected — matches
    // the Python backend's ADMIN_PASSWORD safety rule and migration
    // invariant #7 ("no hardcoded admin credentials").
    var passwordService = new PasswordService();
    passwordService.Validate(password, bootstrap: true);

    var admin = new UserEntity
    {
        Id = Guid.NewGuid(),
        Username = username,
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName,
        PasswordHash = passwordService.Hash(password),
        IsAdmin = true,
        Active = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };
    seedDb.Users.Add(admin);
    await seedDb.SaveChangesAsync().ConfigureAwait(false);

    Console.WriteLine($"[seed-admin] Created admin '{admin.Username}' (id={admin.Id}).");
    return;
}

// ---- Structured logging (Serilog) ----
// Wire Serilog as the host logger so log events flow through the
// redaction processor before any sink writes them. The processor scrubs
// password / API key / bearer / session / prompt / document body fields
// from every property — see SecretRedactionProcessor for the rule set.
// Production deployments plug a non-Console sink in via configuration;
// the contract is just "the enricher runs before the sink".
builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.With(new SecretRedactionProcessor())
        .WriteTo.Console();
});

// Wire the source-generated JSON serializer context so every typed DTO
// the controllers return (FastApiError, OntologyResponse, ChangePreview,
// QueryResponse) hits System.Text.Json's compile-time path. The
// resolver chain keeps the default reflection-based resolver in place
// for the anonymous placeholder payloads the InternalOperationDispatcher
// emits until the Stage 2/3 services land; once those become typed DTOs
// they take the source-gen path and skip reflection entirely. See
// src/OnToPilot/Serialization/OnToPilotJsonContext.cs.
// Wire the source-generated JSON serializer context so every typed DTO
// the controllers return (FastApiError, OntologyResponse, ChangePreview,
// QueryResponse) hits System.Text.Json's compile-time path. The
// resolver chain keeps the default reflection-based resolver in place
// for the anonymous placeholder payloads the InternalOperationDispatcher
// emits until the Stage 2/3 services land; once those become typed DTOs
// they take the source-gen path and skip reflection entirely. See
// src/OnToPilot/Serialization/OnToPilotJsonContext.cs.
//
// PropertyNamingPolicy = SnakeCaseLower: the FastAPI-compatibility
// contract emits snake_case on the wire (see FastApiError.Detail,
// FastApiErrorMiddleware.JobId, PromptSnapshotService.Prompts and the
// comment in FastApiErrorMiddleware line 115). Without an explicit
// naming policy ASP.NET Core's System.Text.Json defaults to camelCase,
// so /api/auth/me returned { "isAdmin": true } while the frontend User
// type reads `is_admin` and the admin UI silently hid for every
// authenticated user. The frontend `api.updateMe` / `createUser` calls
// also send snake_case request bodies; `object body` model binding then
// binds against the same naming policy, so setting it here aligns
// both directions at once. DTOs that need a fixed wire name use
// [JsonPropertyName] and override this default (see FastApiError).
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
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

// Scoped LegacyId allocator. PG path takes a per-table pg_advisory_xact_lock
// so concurrent writers on the same table serialize; SQLite path falls back
// to plain MAX+1 (single-writer DB). See LegacyIdAllocator.cs for rationale.
builder.Services.AddScoped<LegacyIdAllocator>();

// ---- Auth services ----
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordService, PasswordService>();
builder.Services.AddSingleton<KnowledgeSystemAccessService>();

// MCP tool bodies take HttpContext as their first parameter so the
// accessor can read the bearer-stamped principal items; the MCP SDK's
// AIFunctionFactory auto-injects DI-registered types via
// IServiceProviderIsService.IsService. ASP.NET Core ships
// IHttpContextAccessor in the default builder but not HttpContext
// itself, so without this registration the SDK's JSON-RPC path fails
// to bind HttpContext and every tool call throws
// "The arguments dictionary is missing a value for the required
// parameter 'httpContext'." Register HttpContext as a scoped service
// that resolves through the accessor. The IHttpContextAccessor is
// re-registered here defensively because some test hosts (notably
// WebApplicationFactory<Program>) do not preserve every default
// builder registration.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HttpContext>(sp =>
    sp.GetRequiredService<IHttpContextAccessor>().HttpContext
        ?? throw new InvalidOperationException(
            "No current HttpContext on the request scope; MCP tool bodies "
            + "require HttpContext to flow through DI."));

// Bearer-token primitives (scoped to the request DbContext).
builder.Services.AddScoped<IKnowledgeApiTokenService, KnowledgeApiTokenService>();
builder.Services.AddScoped<IMcpTokenService, McpTokenService>();

// Startup recovery hosted services (scoped to a single DbContext per run).
builder.Services.AddScoped<BootstrapAdminService>();
builder.Services.AddScoped<StaleJobRecoveryService>();
builder.Services.AddScoped<LegacyBackfillService>();
// IHttpClientFactory registration — consumed by ProviderService for the
// GET {baseUrl}/models probe in providers.test. ChatClientFactory /
// EmbeddingGeneratorFactory construct HttpClient directly and don't need
// this, but the scoped provider CRUD service does.
builder.Services.AddHttpClient();
// Provider CRUD service — replaces the dispatcher placeholders for
// providers.list / .create / .update / .delete / .test. Scoped so it
// shares the request's OnToPilotDbContext.
builder.Services.AddProviderServices();
// Conflicts slice — detect / list / get_context / dismiss / reopen / resolve
// / reconciliations CRUD. Service is Scoped (shares the request DbContext);
// the optional StoreWrapper + ExtractionJobStore are resolved per-request
// through IServiceProvider so the SQLite contract-test path runs without
// an embedded Oxigraph.
builder.Services.AddConflictServices();
// Knowledge slice — KS CRUD + membership + review stats. Scoped service
// shares the request DbContext and depends on KnowledgeSystemAccessService
// (singleton, registered above) for the Viewer / Editor / Owner gates.
builder.Services.AddKnowledgeServices();
// Documents slice — upload / parse / chunks / move / contribution / delete
// with cross-KS blob ref-count. Scoped DocumentService depends on the
// scoped DbContext; the underlying IBlobStore, IDocumentParser, and
// Chunker are registered as singletons here so a single instance spans
// the whole app. Contract-test factories override IDocumentParser /
// IBlobStore (see AuthTestWebApplicationFactory.ConfigureServices) to
// inject a TestDocumentParser + per-test temp blob root.
var blobRoot = builder.Configuration["OnToPilot:Storage:BlobRoot"]
    ?? Path.Combine(AppContext.BaseDirectory, "data", "blobs");
builder.Services.AddSingleton<IBlobStore>(_ => new LocalCasBlobStore(blobRoot));
builder.Services.AddSingleton<IDocumentParser, DocumentParser>();
builder.Services.AddSingleton<Chunker>(_ => new Chunker(
    size: DocumentService.DefaultChunkSize,
    overlap: DocumentService.DefaultChunkOverlap));
builder.Services.AddDocumentServices();

// ---- Ontology slice ----
// The Oxigraph store is a process-wide singleton (the underlying
// handle is thread-safe + file-locked). Production and the contract
// test factory both honour the same "OnToPilot:Storage:RdfRoot" key
// so a per-test temp dir isolates parallel runs. The OntologyEditor
// wraps the same singleton with the GraphWriteCoordinator lock and
// the per-edit capture / revert helper.
var rdfRoot = builder.Configuration["OnToPilot:Storage:RdfRoot"]
    ?? Path.Combine(AppContext.BaseDirectory, "data", "rdf");
// Oxigraph's Store ctor is sensitive to backslashes on Windows —
// the connection-string parser used for SQLite above silently
// tolerates them, but Oxigraph throws InvalidStoreHandleException
// when the path is not a forward-slash URI. Normalise once here so
// every test / production call site stays portable.
var rdfRootForwardSlash = rdfRoot.Replace('\\', '/');
builder.Services.AddSingleton<StoreWrapper>(_ => new StoreWrapper(rdfRootForwardSlash));
builder.Services.AddSingleton<OntologyEditor>(sp =>
    new OntologyEditor(sp.GetRequiredService<StoreWrapper>()));
builder.Services.AddSingleton<ABoxManager>(sp =>
    new ABoxManager(sp.GetRequiredService<StoreWrapper>()));
builder.Services.AddSingleton<ABoxValidator>(sp =>
    new ABoxValidator(sp.GetRequiredService<StoreWrapper>()));
builder.Services.AddOntologyServices();
builder.Services.AddAboxServices();
builder.Services.AddAboxProvenanceServices();
builder.Services.AddValidationDecisionServices();
// Extraction slice — wires ExtractionOrchestrator + its 8 collaborators as
// singletons. The orchestrator owns the in-process Task.Run job lifecycle
// (ExtractionJobStore singleton + per-job background work), so it must be
// a singleton; every collaborator is either stateless or thread-safe.
// Test factory override (IChatClientFactory -> FakeChatClientFactory.Default)
// from prior commit keeps every WebApplicationFactory-driven extraction
// test green.
builder.Services.AddExtractionServices();
// Vocabulary slice — Scoped VocabularyService wraps SkosManager methods +
// extraction guard + Reader/Writer role gate + audit pre/post diff (B7c
// ABoxService pattern). SkosManager is registered here as a singleton
// (depends on the singleton StoreWrapper); the underlying TerminologyService
// + ExtractionJobStore come from AddExtractionServices above.
builder.Services.AddSingleton<SkosManager>(sp =>
    new SkosManager(sp.GetRequiredService<StoreWrapper>()));
builder.Services.AddVocabularyServices();

builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName,
        _ => { })
    .AddScheme<AuthenticationSchemeOptions, ApiBearerAuthenticationHandler>(
        ApiBearerAuthenticationHandler.SchemeName,
        _ => { })
    .AddScheme<AuthenticationSchemeOptions, ExternalTokenAuthenticationHandler>(
        ExternalTokenAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization();

// ASP.NET Core 10 ships the OpenAPI document at /openapi/v1.json when both
// the transformer services and the endpoint mapping are registered. The
// inventory test reads this URL, so the registration has to live next to
// the rest of the pipeline wiring.
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();

// ---- OpenTelemetry ----
// Subscribe to every OnToPilot.* ActivitySource (defined in
// OnToPilot.Observability.Telemetry) and the shared "OnToPilot" meter.
// ASP.NET Core + Npgsql instrumentation provide the rest. The brief
// requires this exact wiring so a new source / meter only needs to be
// added in Telemetry.cs — no Program.cs edit.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "OnToPilot",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0"))
    .WithTracing(tracing => tracing
        .AddSource("OnToPilot.*")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddNpgsql())
    .WithMetrics(metrics => metrics
        .AddMeter("OnToPilot")
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddProcessInstrumentation());

// ---- MCP transport ----
// The MCP transport is registered with the SDK's HttpServer transport in
// stateless mode (no session affinity, fresh server context per request).
// The token / role checks live in McpTokenAuthenticationMiddleware (HTTP
// layer) and McpPrincipalAccessor (per-call real-time lookup); the SDK
// only owns the JSON-RPC envelope. The 1 MiB request limit + DNS-rebind
// guard keep the public endpoint within the brief-mandated bounds.
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
    {
        Name = "OntoPilot",
        Version = "1.0.0",
    };
})
.WithHttpTransport(options =>
{
    options.Stateless = true;
})
.WithTools<OnToPilotMcpTools>()
.WithResources<OnToPilotMcpResources>()
.WithPrompts<OnToPilotMcpPrompts>();

// Principal accessor is scoped to the request DbContext so the per-call
// role re-resolution lives on the same change tracker as the rest of the
// request. The middleware reads it through DI too; both registrations
// resolve to the same scoped instance per request.
builder.Services.AddScoped<McpPrincipalAccessor>();

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

// DNS-rebinding protection runs FIRST on /mcp so a malicious origin
// sending a malformed bearer cannot elicit a 401 envelope before the
// host check rejects the request — the 401 would otherwise leak that
// the endpoint exists. This is the same allowlist FastMCP / the brief
// both call out: localhost, 127.0.0.1, [::1], and the configured
// OnToPilot:PublicHost. Production deployments override
// OnToPilot:PublicHost via configuration.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/mcp"),
    branch =>
    {
        branch.Use(async (ctx, next) =>
        {
            var host = ctx.Request.Host.Host;
            var allowed = new[]
            {
                "localhost",
                "127.0.0.1",
                "[::1]",
                ctx.RequestServices.GetRequiredService<IConfiguration>()["OnToPilot:PublicHost"] ?? "localhost",
            };
            if (!allowed.Any(a => string.Equals(a, host, StringComparison.OrdinalIgnoreCase)))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    new { detail = "DNS rebinding rejected: host header not allowed." });
                await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted).ConfigureAwait(false);
                return;
            }
            await next().ConfigureAwait(false);
        });
    });

// MCP transport: bearer-token authentication runs ahead of the JSON-RPC
// handler so the SDK sees an authenticated principal on every tool call.
// Only /mcp is authenticated; the rest of the pipeline keeps its existing
// session / bearer / contract-test schemes. The host-header guard above
// runs first so a non-allowed host never sees the 401 envelope.
app.UseMiddleware<McpTokenAuthenticationMiddleware>();

// Cap the MCP request body at 1 MiB. Anything larger is rejected with
// 413 by the Kestrel form-options reader so a malicious client cannot
// pin a worker thread on a 4 GiB JSON-RPC payload.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/mcp"),
    branch =>
    {
        branch.Use(async (ctx, next) =>
        {
            var feature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
            {
                feature.MaxRequestBodySize = 1024 * 1024;
            }
            await next().ConfigureAwait(false);
        });
    });

app.MapControllers();
app.MapOpenApi();

// Wire the SDK's Streamable HTTP transport at /mcp. The endpoint routes
// POST initialize / tools/list / tools/call / prompts/list / resources/list
// into the registered server primitives; the stateless flag means every
// request is independent and no Mcp-Session-Id header is required.
// Auth is handled by McpTokenAuthenticationMiddleware ahead of the SDK
// pipeline; we deliberately do NOT call .RequireAuthorization() so the
// SDK does not fall through to the session-cookie scheme and emit a
// confusing "Not authenticated" envelope before our bearer check runs.
app.MapMcp("/mcp");

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
