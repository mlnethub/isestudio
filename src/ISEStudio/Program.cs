using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using Npgsql;
using ISEStudio.Api;
using ISEStudio.Application.Integration;
using ISEStudio.Authentication;
using ISEStudio.Authorization;
using ISEStudio.Configuration;
using ISEStudio.Conflicts;
using ISEStudio.Documents;
using ISEStudio.EntityResolution;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Infrastructure.Startup;
using ISEStudio.Integration;
using ISEStudio.Knowledge;
using ISEStudio.Mcp;
using ISEStudio.Observability;
using ISEStudio.Ontology;
using ISEStudio.Exports;
using ISEStudio.Parsing;
using ISEStudio.Prompts;
using ISEStudio.Sparql;
using ISEStudio.Providers;
using ISEStudio.Serialization;
using ISEStudio.Storage;
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

    var provider = migrateConfig["ISEStudio:Persistence:Provider"] ?? "npgsql";
    var optionsBuilder = new DbContextOptionsBuilder<ISEStudioDbContext>();
    if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var sqlite = migrateConfig["ISEStudio:Persistence:SqliteConnection"]
            ?? "Data Source=:memory:";
        optionsBuilder.UseSqlite(sqlite);
    }
    else
    {
        var npgsql = migrateConfig["ISEStudio:Persistence:ConnectionString"]
            ?? throw new InvalidOperationException(
                "ISEStudio:Persistence:ConnectionString is required for the --migrate entry point.");
        optionsBuilder.UseNpgsql(npgsql);
    }

    await using var migrateDb = new ISEStudioDbContext(optionsBuilder.Options);
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
    var seedProvider = seedConfig["ISEStudio:Persistence:Provider"] ?? "npgsql";
    var seedOptionsBuilder = new DbContextOptionsBuilder<ISEStudioDbContext>();
    if (string.Equals(seedProvider, "sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var sqlite = seedConfig["ISEStudio:Persistence:SqliteConnection"]
            ?? "Data Source=:memory:";
        seedOptionsBuilder.UseSqlite(sqlite);
    }
    else
    {
        var npgsql = seedConfig["ISEStudio:Persistence:ConnectionString"]
            ?? throw new InvalidOperationException(
                "ISEStudio:Persistence:ConnectionString is required for --seed-admin.");
        seedOptionsBuilder.UseNpgsql(npgsql);
    }

    await using var seedDb = new ISEStudioDbContext(seedOptionsBuilder.Options);

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
// src/ISEStudio/Serialization/ISEStudioJsonContext.cs.
// Wire the source-generated JSON serializer context so every typed DTO
// the controllers return (FastApiError, OntologyResponse, ChangePreview,
// QueryResponse) hits System.Text.Json's compile-time path. The
// resolver chain keeps the default reflection-based resolver in place
// for the anonymous placeholder payloads the InternalOperationDispatcher
// emits until the Stage 2/3 services land; once those become typed DTOs
// they take the source-gen path and skip reflection entirely. See
// src/ISEStudio/Serialization/ISEStudioJsonContext.cs.
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
        ISEStudioJsonContext.Default,
        new DefaultJsonTypeInfoResolver());
});

// Single in-process dispatcher + facade. Controllers depend only on the
// facade; the dispatcher is the implementation seam for swapping in
// per-operation delegates as Stage 2/3 services stabilise.
//
// IMPORTANT lifetime note: the dispatcher MUST be Scoped, not Singleton.
// It captures an IServiceProvider in its ctor and resolves the scoped
// services (KnowledgeService / ConflictService / DocumentService /
// ABoxService / VocabularyService / ProviderService / OntologyService /
// ISEStudioDbContext) per call via _services.GetService. Registering it
// as a Singleton makes the captured provider the root one, which turns
// every resolved scoped service into a captive dependency: every
// concurrent HTTP request shares the same DbContext instance, and EF
// Core throws "A second operation was started on this context instance"
// the moment two requests overlap. Registering Scoped makes the captured
// provider the request scope, so each request gets its own DbContext.
builder.Services.AddScoped<IInternalOperationDispatcher, InternalOperationDispatcher>();
builder.Services.AddScoped<IIntegrationApiFacade, IntegrationApiFacade>();

builder.Services.Configure<ISEStudioOptions>(
    builder.Configuration.GetSection(ISEStudioOptions.SectionName));

// Wire the EF Core DbContext via the factory-only pattern.
//
// Why factory-only (no `AddDbContext<>`):
//   1. Background singletons (`ExtractionJobStore`, `ExportJobStore`) need
//      to open fresh contexts without sharing an HttpContext-bound tracker.
//   2. ASP.NET Core request handlers need a scoped `ISEStudioDbContext` for
//      per-request tracking.
//
// The historical layout — `AddDbContext<>` (registers Scoped
// `DbContextOptions<T>`) plus `AddDbContextFactory<>` (Singleton, tries to
// consume those same Scoped `DbContextOptions<T>`) — produced a captive
// dependency that only surfaced under `dotnet run`'s development-time
// ServiceProvider validation. See `docs/superpowers/specs/2026-08-23-p0-blocker-captive-and-prompt-gap.md`.
//
// The single factory registration below produces the `DbContextOptions<T>`
// exactly once (with lifetime = Singleton) so both consumers — request
// handlers via the `AddScoped<ISEStudioDbContext>` proxy below, and
// background services via `IDbContextFactory<TContext>` — share the same
// options graph and never trip scope validation.
builder.Services.AddDbContextFactory<ISEStudioDbContext>((sp, options) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var provider = config["ISEStudio:Persistence:Provider"] ?? "npgsql";
    if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var sqlite = config["ISEStudio:Persistence:SqliteConnection"]
            ?? "Data Source=:memory:";
        options.UseSqlite(sqlite);
    }
    else
    {
        var npgsql = config["ISEStudio:Persistence:ConnectionString"]
            ?? "Host=localhost;Port=5432;Database=isestudio;Username=postgres;Password=postgres";
        options.UseNpgsql(npgsql);
    }
});

// Scoped proxy so per-request services can still inject `ISEStudioDbContext`
// directly (37 services do — see `Ontology/HistoryService.cs:14` etc.).
// The factory is owned by the root provider; this proxy hands each request
// its own short-lived context via `CreateDbContext()`, keeping the
// `DbContextOptions` graph compatible with both lifetimes.
builder.Services.AddScoped<ISEStudioDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());

// Singleton so the dispatcher's "find any active extraction" guard does
// not have to share state with the request-scoped DbContext; the store
// uses its own IDbContextFactory.
builder.Services.AddSingleton<ExtractionJobStore>();

builder.Services.AddScoped<ISEStudio.Audit.AuditLogService>();

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
// Token CRUD orchestrator — replaces the placeholder arms in the
// dispatcher for tokens.* (list / create / revoke / reveal) and
// mcp_tokens.* (list / create / revoke). Scoped because it shares the
// request DbContext + audit + clock with the bearer-token primitives.
builder.Services.AddScoped<TokenManagementService>();
// Application service facade for the seven tokens.* / mcp_tokens.*
// dispatcher arms. Scoped — shares the request DbContext with
// TokenManagementService through the constructor.
builder.Services.AddScoped<ITokenApplicationService, TokenApplicationService>();
// User CRUD (admin side). auth.update_me + auth.list_users / create /
// update / delete_user. auth.login / logout / me stay inline in
// AuthController because they own the session-cookie plumbing. Scoped
// because it shares the request DbContext with AuthSessions +
// KSGrants + McpUserTokens so the cascade-on-deactivate / -delete
// paths run in a single transaction.
builder.Services.AddScoped<AuthService>();
// Keycloak SSO 用户同步(每个 JwtBearer OnTokenValidated 调用一次)。
// Scoped — 与请求 DbContext 共享。
builder.Services.AddScoped<SsoUserSyncService>();
// SsoOptions 绑定(spec §4.1)。Authority 为空 = SSO 整体禁用,见
// AddJwtBearer 条件注册(Task 4)。
builder.Services.Configure<SsoOptions>(
    builder.Configuration.GetSection(SsoOptions.SectionName));
// Application service facade for the five admin-side auth.* dispatcher
// arms (update_me / list_users / create_user / update_user /
// delete_user). Scoped — shares the request DbContext with AuthService
// through the constructor.
builder.Services.AddScoped<IAuthApplicationService, AuthApplicationService>();

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
// shares the request's ISEStudioDbContext.
builder.Services.AddProviderServices();
// Settings slice — singleton system-config CRUD (list_models / get /
// update). Scoped because it shares the request DbContext with the
// provider validation paths inside UpdateAsync.
builder.Services.AddScoped<ISEStudio.Settings.SettingsService>();
// Application service facade for the three settings.* dispatcher arms
// (list_models / get / update). Scoped — shares the request DbContext
// with SettingsService through the constructor.
builder.Services.AddScoped<ISettingsApplicationService, SettingsApplicationService>();
// Conflicts slice — detect / list / get_context / dismiss / reopen / resolve
// / reconciliations CRUD. Service is Scoped (shares the request DbContext);
// the optional StoreWrapper + ExtractionJobStore are resolved per-request
// through IServiceProvider so the SQLite contract-test path runs without
// an embedded Oxigraph.
builder.Services.AddConflictServices();
// Entity-resolution slice — queue / decisions / resolve (match|new) / revoke /
// edit_reason. Scoped; ABoxManager + StoreWrapper are resolved per-request via
// the singleton registrations above so the SQLite contract-test path runs
// without an embedded Oxigraph.
builder.Services.AddResolutionServices();
builder.Services.AddSparqlServices();
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
//
// Blob backend is selected by the Storage:Endpoint config key (slice 9):
// when the key is present, point at MinIO/S3-compatible object storage;
// otherwise fall back to the local CAS-on-disk backend so dev runs and
// unit tests stay self-contained.
var blobRoot = builder.Configuration["ISEStudio:Storage:BlobRoot"]
    ?? Path.Combine(AppContext.BaseDirectory, "data", "blobs");
var minioEndpoint = builder.Configuration["ISEStudio:Storage:Endpoint"];
if (!string.IsNullOrWhiteSpace(minioEndpoint))
{
    var minioAccess = builder.Configuration["ISEStudio:Storage:AccessKey"] ?? "";
    var minioSecret = builder.Configuration["ISEStudio:Storage:SecretKey"] ?? "";
    var minioBucket = builder.Configuration["ISEStudio:Storage:Bucket"] ?? "";
    var minioUseSsl = builder.Configuration.GetValue<bool?>("ISEStudio:Storage:UseSsl") ?? true;
    var endpoint = minioUseSsl
        ? minioEndpoint
        : (minioEndpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? minioEndpoint
            : "http://" + minioEndpoint);
    builder.Services.AddSingleton<MinioBlobStore>(_ =>
        MinioBlobStore.Create(endpoint, minioAccess, minioSecret, minioBucket));
    builder.Services.AddSingleton<IBlobStore>(sp =>
        sp.GetRequiredService<MinioBlobStore>());
    // Closes the P1-2 backend gap (slice 9 follow-up): a fresh MinIO
    // instance no longer surfaces as a 500 on the first document upload.
    // See `MinioBucketInitializer` for the startup contract.
    builder.Services.AddHostedService<MinioBucketInitializer>();
}
else
{
    builder.Services.AddSingleton<IBlobStore>(_ => new LocalCasBlobStore(blobRoot));
}
builder.Services.AddSingleton<IDocumentParser, DocumentParser>();
builder.Services.AddSingleton<Chunker>(_ => new Chunker(
    size: DocumentService.DefaultChunkSize,
    overlap: DocumentService.DefaultChunkOverlap));
builder.Services.AddDocumentServices();

// ---- Ontology slice ----
// The Oxigraph store is a process-wide singleton (the underlying
// handle is thread-safe + file-locked). Production and the contract
// test factory both honour the same "ISEStudio:Storage:RdfRoot" key
// so a per-test temp dir isolates parallel runs. The OntologyEditor
// wraps the same singleton with the GraphWriteCoordinator lock and
// the per-edit capture / revert helper.
var rdfRoot = builder.Configuration["ISEStudio:Storage:RdfRoot"]
    ?? Path.Combine(AppContext.BaseDirectory, "data", "rdf");
// Oxigraph's Store ctor is sensitive to backslashes on Windows —
// the connection-string parser used for SQLite above silently
// tolerates them, but Oxigraph throws InvalidStoreHandleException
// when the path is not a forward-slash URI. Normalise once here so
// every test / production call site stays portable.
var rdfRootForwardSlash = rdfRoot.Replace('\\', '/');
// Oxigraph is disk-backed (RocksDB) and the workspace path is configured for
// long-running production / dev hosts. The contract-test factory
// (ApiContractWebApplicationFactory) sets the environment to "Testing" with
// no provisioned RDF root, so opening the RocksDB handle there throws
// "Invalid RocksDB error message" and turns every ontology/vocabulary
// request into a 500. Only wire the singleton when we're actually running
// somewhere that has a workspace to point at; in non-Dev/Prod (Testing +
// any other transient env), register a null StoreWrapper so
// ConflictService — whose ctor accepts `StoreWrapper?` and falls back to
// "return what's already in DB" (ConflictService.cs:102-108) — can run the
// SQL contract path without an embedded Oxigraph.
if (builder.Environment.IsDevelopment() || builder.Environment.IsProduction())
{
    builder.Services.AddSingleton<StoreWrapper>(_ => new StoreWrapper(rdfRootForwardSlash));
}
else
{
#pragma warning disable CS8634
    builder.Services.AddSingleton<StoreWrapper?>(_ => null);
#pragma warning restore CS8634
}
builder.Services.AddSingleton<OntologyEditor>(sp =>
    new OntologyEditor(sp.GetService<StoreWrapper>()));
builder.Services.AddSingleton<ABoxManager>(sp =>
    new ABoxManager(sp.GetService<StoreWrapper>()));
builder.Services.AddSingleton<ABoxValidator>(sp =>
    new ABoxValidator(sp.GetService<StoreWrapper>()));
builder.Services.AddOntologyServices();
builder.Services.AddPromptServices();
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
    new SkosManager(sp.GetService<StoreWrapper>()));
builder.Services.AddVocabularyServices();

// ---- Releases exports (slice 7b) ----
// ExportArtifactStore writes per-job N-Quads shards under
// `{ExportRoot}/{publicId}/{jobId}/…`. The root is independent of
// the RDF workspace root so a future swap to object storage (MinIO) only
// touches the store implementation. The contract-test factory overrides
// the path via configuration (per-test temp dir).
var exportRoot = builder.Configuration["ISEStudio:Storage:ExportRoot"]
    ?? Path.Combine(AppContext.BaseDirectory, "data", "exports");
Directory.CreateDirectory(exportRoot);
builder.Services.AddSingleton(_ => new ExportArtifactStore(exportRoot));
builder.Services.AddExportServices();

// ---- Authentication ----
// 默认 scheme 是 PolicyScheme:请求带 Authorization: Bearer 头 →
// Keycloak JwtBearer(SSO);否则 → SessionCookie(本地账号)。ApiBearer /
// ExternalToken 在各自 controller 显式标注 scheme,不走默认转发。
// Keycloak 未配置(Authority 空)→ 不注册 JwtBearer,default 保持
// SessionCookie,现有行为逐字节不变(spec §2 配置驱动激活)。
const string ForwardScheme = "ForwardScheme";
var ssoOptions = builder.Configuration
    .GetSection(SsoOptions.SectionName)
    .Get<SsoOptions>() ?? new SsoOptions();

var authBuilder = builder.Services.AddAuthentication(options =>
{
    if (ssoOptions.IsEnabled)
    {
        options.DefaultScheme = ForwardScheme;
        options.DefaultAuthenticateScheme = ForwardScheme;
        options.DefaultChallengeScheme = ForwardScheme;
    }
    else
    {
        // SSO 关闭时保持原行为:五个默认全部指向 SessionCookie,
        // 等价旧的 AddAuthentication(SessionAuthenticationHandler.SchemeName)
        // 字符串重载(它同时设 DefaultSignIn/SignOut)。缺失会让任何
        // [Authorize] 请求 500 "no DefaultChallengeScheme"。
        options.DefaultScheme = SessionAuthenticationHandler.SchemeName;
        options.DefaultAuthenticateScheme = SessionAuthenticationHandler.SchemeName;
        options.DefaultChallengeScheme = SessionAuthenticationHandler.SchemeName;
        options.DefaultSignInScheme = SessionAuthenticationHandler.SchemeName;
        options.DefaultSignOutScheme = SessionAuthenticationHandler.SchemeName;
    }
});
if (ssoOptions.IsEnabled)
{
    authBuilder.AddPolicyScheme(ForwardScheme, "forward", o =>
    {
        o.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? JwtBearerDefaults.AuthenticationScheme
                : SessionAuthenticationHandler.SchemeName;
    });
}
authBuilder
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName,
        _ => { })
    .AddScheme<AuthenticationSchemeOptions, ApiBearerAuthenticationHandler>(
        ApiBearerAuthenticationHandler.SchemeName,
        _ => { })
    .AddScheme<AuthenticationSchemeOptions, ExternalTokenAuthenticationHandler>(
        ExternalTokenAuthenticationHandler.SchemeName,
        _ => { });

if (ssoOptions.IsEnabled)
{
    authBuilder.AddJwtBearer(o =>
    {
        o.Authority = ssoOptions.Authority;
        // 默认必须 https;容器内 http 部署显式置 false。
        o.RequireHttpsMetadata = ssoOptions.RequireHttpsMetadata;
        // claim 保持 Keycloak 原名(不映射成 WS-Federation 长 URI)。
        o.MapInboundClaims = false;
        // 容器部署双 URL:Authority(iss 校验)是浏览器可见地址,metadata
        // 从容器内地址拉(见 deploy 计划 Task 1)。空 = 默认从 Authority 派生。
        if (!string.IsNullOrWhiteSpace(ssoOptions.MetadataAddress))
        {
            o.MetadataAddress = ssoOptions.MetadataAddress;
        }
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = ssoOptions.Authority,
            // aud 恒为 account,无判定价值;azp 断言在 OnTokenValidated。
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "preferred_username",
        };
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                // azp 门:Keycloak public client 的 access_token 里
                // aud 恒为 account,azp 等于 clientId 才是真凭据。
                if (ctx.Principal is null
                    || !string.Equals(
                        ctx.Principal.FindFirst("azp")?.Value,
                        ssoOptions.ClientId, StringComparison.Ordinal))
                {
                    ctx.Fail($"azp is not {ssoOptions.ClientId}");
                    return;
                }

                // 用户同步(建行/刷新)+ Items 挂点 ——下
                // KSRoleAuthorize / ResolveActor / me 全部复用。
                // 必须先 sync 才知道 IsAdmin,然后再把可配置 admin 角色
                // 名字(realm_access.roles 默认 "admin")映射成与本地
                // SessionCookie 路径一致的 ClaimTypes.Role "Admin" claim,
                // 让 Policies.AdminOnly 的 RequireRole("Admin") 不必区分
                // 本地 / SSO 凭据来源。
                using var scope = ctx.HttpContext.RequestServices.CreateScope();
                var sync = scope.ServiceProvider
                    .GetRequiredService<SsoUserSyncService>();
                UserEntity user;
                try
                {
                    user = await sync.SyncAsync(
                        ctx.Principal, ctx.HttpContext.RequestAborted);
                }
                catch (UnauthorizedAccessException ex)
                {
                    // 同步后的用户被禁用 — 走 401 通道而非未处理异常 500。
                    ctx.Fail(ex.Message);
                    return;
                }

                if (ctx.Principal.Identity is ClaimsIdentity identity)
                {
                    // realm_access.roles 摊平 —— 保留 Keycloak 原角色名
                    // (viewer / editor / ...)供服务层做细粒度判别。
                    foreach (var role in SsoClaimMapping.RealmRoles(ctx.Principal))
                        identity.AddClaim(new Claim(ClaimTypes.Role, role));
                    if (user.IsAdmin)
                        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
                }

                ctx.HttpContext.Items[SessionAuthenticationHandler.UserItemKey] = user;
            },
        };
    });
}
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.AdminOnly, policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("Admin"));

    options.AddPolicy(Policies.KSOwnerOnly, policy =>
        policy.RequireAuthenticatedUser()
              .RequireAssertion(ctx => ctx.User.IsInRole("Admin"))); // hook for Step 4
});

// ASP.NET Core 10 ships the OpenAPI document at /openapi/v1.json when both
// the transformer services and the endpoint mapping are registered. The
// inventory test reads this URL, so the registration has to live next to
// the rest of the pipeline wiring.
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();

// ---- OpenTelemetry ----
// Subscribe to every ISEStudio.* ActivitySource (defined in
// ISEStudio.Observability.Telemetry) and the shared "ISEStudio" meter.
// ASP.NET Core + Npgsql instrumentation provide the rest. The brief
// requires this exact wiring so a new source / meter only needs to be
// added in Telemetry.cs — no Program.cs edit.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "ISEStudio",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0"))
    .WithTracing(tracing => tracing
        .AddSource("ISEStudio.*")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddNpgsql())
    .WithMetrics(metrics => metrics
        .AddMeter("ISEStudio")
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
        Name = "ISEStudio",
        Version = "1.0.0",
    };
})
.WithHttpTransport(options =>
{
    options.Stateless = true;
})
.WithTools<ISEStudioMcpTools>()
.WithResources<ISEStudioMcpResources>()
.WithPrompts<ISEStudioMcpPrompts>();

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

// Stamp the SKOS vocabulary prefix from configuration BEFORE building
// the host so any service that captures SkosVocab.IseStudio (e.g.
// ShaclValidator, SkosManager) at construction time sees the configured
// value rather than the default.
SkosVocab.Configure(builder.Configuration["ISEStudio:VocabNamespace"]
    ?? new ISEStudioOptions().VocabNamespace);

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
// ISEStudio:PublicHost. Production deployments override
// ISEStudio:PublicHost via configuration.
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
                ctx.RequestServices.GetRequiredService<IConfiguration>()["ISEStudio:PublicHost"] ?? "localhost",
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
// For the SQLite provider (tests + dev) we EnsureCreated here so the
// bootstrap check has a table to query. For PostgreSQL we trust the
// deploy-time migrations to have applied the InitialCompatibility
// migration; the schema must exist before this process starts.
//
// EnsureCreated runs in every environment, including the contract-test
// "Testing" env. Previously it was skipped there on the assumption that
// tests seed their own tables through WebApplicationFactory.CreateDbContext,
// but the contract tests exercise ConflictService.ListAsync which hits
// "no such table: knowledgesystem" without EnsureCreated. The Testing env
// SQLite file is per-test-session (in-memory or temp), so there is no
// existing data for EnsureCreated to clobber. The bootstrap / recovery
// services remain gated below because they exit with code 17 on an empty
// user table, which would prevent the WebApplicationFactory test host
// from booting.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
    if (db.Database.IsSqlite())
    {
        await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
    }
}

if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
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
