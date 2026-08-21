using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Llm;
using OnToPilot.Storage;

namespace OnToPilot.ApiContract.Tests.Baseline;

/// <summary>
/// Lightweight test host for the OnToPilot web project used by the
/// contract inventory tests. Sets the environment to <c>"Testing"</c> so
/// the bootstrap-recovery check (which refuses to start against an
/// empty users table) is skipped — the contract tests do not need a
/// seeded database.
///
/// <para>Forces the EF Core provider to <c>sqlite</c> with a per-factory
/// file-backed database so the contract theory test (which spawns one
/// factory per operation case) doesn't try to talk to the local
/// PostgreSQL instance the production code points at by default. The
/// file lives under <c>%TEMP%</c> and is deleted on dispose so two
/// parallel test runs never collide.</para>
///
/// <para>Adds a <c>ContractTest</c> authentication scheme that authenticates
/// every request as an admin user. The contract test only asserts HTTP
/// status + response schema — it doesn't care about session validation,
/// so going through the full login flow would only add flake without
/// buying any signal.</para>
///
/// <para>Wires a per-factory Oxigraph <c>StoreWrapper</c> rooted under
/// <c>%TEMP%</c> so the ontology-touching dispatcher arms
/// (<c>ks.get</c>, <c>ks.update</c>, <c>abox.*</c>, etc.) don't crash
/// with <c>ArgumentNullException("store")</c> when the production
/// code asks the StoreWrapper to load its graph context. Mirrors the
/// wiring the auth/access test host
/// (<c>OnToPilot.Tests.Authentication.AuthTestWebApplicationFactory</c>)
/// uses for its in-process integration tests; without this the harness
/// is racing the factory's <c>StoreWrapper</c> registration, which is
/// only resolved when the production composition root reads the
/// <c>OnToPilot:Ontology:RocksDbPath</c> configuration value.</para>
/// </summary>
internal sealed class ApiContractWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestSchemeName = "ContractTest";

    private readonly string _sqlitePath = Path.Combine(
        Path.GetTempPath(),
        $"ontopilot-contract-{Guid.NewGuid():N}.db");
    private readonly string _rdfRoot = Path.Combine(
        Path.GetTempPath(),
        $"ontopilot-contract-rdf-{Guid.NewGuid():N}")
        // Oxigraph's Store ctor refuses backslashes on Windows;
        // normalise here so the production wiring (which uses
        // forward slashes) and the test wiring share the same
        // happy-path.
        .Replace('\\', '/');

    // Per-factory blob root so the documents/upload contract case
    // (which writes the uploaded file through LocalCasBlobStore) has
    // an isolated on-disk root and doesn't collide with parallel
    // factories. Mirrors AuthTestWebApplicationFactory.
    private readonly string _blobRoot = Path.Combine(
        Path.GetTempPath(),
        $"ontopilot-contract-blob-{Guid.NewGuid():N}");

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OnToPilot:Persistence:Provider"] = "sqlite",
                ["OnToPilot:Persistence:SqliteConnection"] = $"Data Source={_sqlitePath}",
            });
        });
        builder.ConfigureServices(services =>
        {
            // Always-authenticating test scheme so [Authorize] gates on
            // the internal controllers don't fail the contract test with
            // 401s. We register a NEW authentication setup on top of the
            // production chain so [Authorize] with no explicit scheme
            // picks the test scheme instead of SessionCookie / ApiBearer.
            services.AddAuthentication(defaultScheme: TestSchemeName)
                .AddScheme<AuthenticationSchemeOptions, ContractTestAuthHandler>(
                    TestSchemeName, _ => { });

            // Per-factory Oxigraph handle so the ontology-touching
            // dispatcher arms (KnowledgeService, ABoxService,
            // OntologyService, VocabularyService, …) don't crash with
            // ArgumentNullException("store") when they ask the
            // StoreWrapper for its current graph state. The
            // production composition root registers a StoreWrapper
            // scoped to OnToPilot:Ontology:RocksDbPath; here we
            // explicitly replace that registration with a per-factory
            // singleton rooted under %TEMP% so the contract theory
            // test (which spawns one factory per operation case)
            // doesn't collide on the shared RocksDB lock.
            var rdfDescriptors = services
                .Where(d => d.ServiceType.FullName == "OnToPilot.Ontology.StoreWrapper")
                .ToList();
            foreach (var desc in rdfDescriptors) services.Remove(desc);
            services.AddSingleton(typeof(OnToPilot.Ontology.StoreWrapper),
                _ => new OnToPilot.Ontology.StoreWrapper(_rdfRoot));

            // Per-factory blob store so the documents/upload arm (which
            // bypasses the facade and calls DocumentService.UploadAsync
            // directly) has a real LocalCasBlobStore to write to instead
            // of the production registration pointing at an unconfigured
            // path. Mirrors AuthTestWebApplicationFactory.
            var blobDescriptors = services
                .Where(d => d.ServiceType == typeof(IBlobStore))
                .ToList();
            foreach (var desc in blobDescriptors) services.Remove(desc);
            services.AddSingleton<IBlobStore>(_ => new LocalCasBlobStore(_blobRoot));

            // Override the production IChatClientFactory with the
            // shared test fake so the extraction dispatcher arms
            // (run / run_combined / run_instances) don't NRE on
            // ChatClientFactory.Create when the harness doesn't
            // preconfigure an LLM provider. NoopChatClientFactory
            // returns a throwaway IChatClient that no-ops every call;
            // the contract theory test only asserts the wire envelope,
            // so the fake's response stream is never inspected.
            services.RemoveAll<IChatClientFactory>();
            services.AddSingleton<IChatClientFactory>(NoopChatClientFactory.Instance);
        });
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (File.Exists(_sqlitePath)) File.Delete(_sqlitePath); }
            catch { /* best-effort cleanup */ }
            try { if (Directory.Exists(_rdfRoot)) Directory.Delete(_rdfRoot, recursive: true); }
            catch { /* best-effort cleanup */ }
            try { if (Directory.Exists(_blobRoot)) Directory.Delete(_blobRoot, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Authentication handler that authenticates every request as the seed
/// admin. Stamps <see cref="SessionAuthenticationHandler.UserItemKey"/>
/// with a synthetic admin <see cref="UserEntity"/> so the controllers
/// that read the session item continue to work without a real session.
/// </summary>
internal sealed class ContractTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public ContractTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // DemoUserId must match the row the harness seeds in
        // ApiContractScenario.SeedDemoEntitiesAsync. See that file
        // for the rationale behind the constant being a non-zero
        // Guid.
        var admin = new UserEntity
        {
            Id = ApiContractScenario.DemoUserId,
            Username = "contract-admin",
            DisplayName = "Contract Admin",
            IsAdmin = true,
            Active = true,
            PasswordHash = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        Context.Items[SessionAuthenticationHandler.UserItemKey] = admin;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new(ClaimTypes.Name, admin.Username),
            new(ClaimTypes.Role, "Admin"),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Minimal <see cref="IChatClientFactory"/> fake used by the contract
/// test host. Mirrors the role of <c>FakeChatClientFactory</c> in
/// <c>OnToPilot.Tests.Extraction</c> but lives here so the
/// ApiContract.Tests project doesn't need a project reference back into
/// the integration test assembly. Returns a delegate-backed
/// <see cref="IChatClient"/> that silently completes every request;
/// the contract theory test only asserts the wire envelope, never the
/// LLM response payload.
/// </summary>
internal sealed class NoopChatClientFactory : IChatClientFactory
{
    public static readonly NoopChatClientFactory Instance = new();

    private NoopChatClientFactory() { }

    public IChatClient Create(LlmProviderConfig config) => new NoopChatClient();
}

internal sealed class NoopChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new(nameof(NoopChatClient));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Yield once so the caller sees an actual async completion;
        // returning CompletedTask would still satisfy the contract but
        // the explicit await keeps the call shape realistic.
        await Task.Yield();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}