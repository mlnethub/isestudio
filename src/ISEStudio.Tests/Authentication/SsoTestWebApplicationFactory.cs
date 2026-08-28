using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ISEStudio.Authentication;
using ISEStudio.Documents;
using ISEStudio.Llm;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using ISEStudio.Tests.Documents;
using ISEStudio.Tests.Extraction;

namespace ISEStudio.Tests.Authentication;

/// <summary>
/// Web host for the Keycloak JwtBearer integration tests. Configures
/// <see cref="SsoOptions"/> so <c>Authority</c> points at the in-process
/// fake issuer, and replaces the JwtBearer's discovery / JWKS fetcher with
/// a mock <see cref="HttpMessageHandler"/> — no real network is hit. The
/// rest of the test wiring (per-test SQLite, isolated Oxigraph / blob /
/// export roots, fake LLM + document parser) matches
/// <see cref="AuthTestWebApplicationFactory"/> because SSO tests still
/// need the host to start up cleanly even though they only touch
/// <c>/api/auth</c>.
/// </summary>
/// <remarks>
/// SsoOptions values are injected via environment variables (with the
/// <c>__</c> separator that ASP.NET Core uses for nested keys) rather
/// than the in-memory configuration source. <see cref="Program"/> reads
/// the SsoOptions section during the service-collection phase to decide
/// whether to register JwtBearer — and that decision needs to see the
/// fake issuer's Authority. Environment variables are the first source
/// the WebApplicationBuilder consults, so they're in scope well before
/// the ConfigureAppConfiguration callbacks fire.
/// </remarks>
public sealed class SsoTestWebApplicationFactory : WebApplicationFactory<Program>
{
    public TestJwtIssuer Issuer { get; } = new();

    private readonly string _sqlitePath;
    private readonly string _blobRoot;
    private readonly string _rdfRoot;
    private readonly string _exportRoot;
    private readonly System.Collections.Generic.List<string> _envVarsToCleanup = new();

    public SsoTestWebApplicationFactory()
    {
        var testId = Guid.NewGuid().ToString("N");
        _sqlitePath = Path.Combine(Path.GetTempPath(),
            $"isestudio-sso-tests-{testId}.db").Replace('\\', '/');
        _blobRoot = Path.Combine(Path.GetTempPath(),
            $"isestudio-sso-blob-{testId}");
        _rdfRoot = Path.Combine(Path.GetTempPath(),
            $"isestudio-sso-rdf-{testId}").Replace('\\', '/');
        _exportRoot = Path.Combine(Path.GetTempPath(),
            $"isestudio-sso-exports-{testId}");

        // Inject SsoOptions via env vars so Program.cs's startup-time
        // SsoOptions evaluation sees them. ConfigureAppConfiguration
        // callbacks fire later (during Build) and would be too late for
        // the conditional AddJwtBearer registration.
        SetEnv("ISEStudio__Auth__Keycloak__Authority", Issuer.Authority);
        SetEnv("ISEStudio__Auth__Keycloak__ClientId", Issuer.ClientId);
        SetEnv("ISEStudio__Auth__Keycloak__RequireHttpsMetadata", "false");
        SetEnv("ISEStudio__Auth__Keycloak__AdminRole", "admin");
    }

    private void SetEnv(string key, string value)
    {
        System.Environment.SetEnvironmentVariable(key, value);
        _envVarsToCleanup.Add(key);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ISEStudio:CookieSecure"] = "false",
                ["ISEStudio:SessionTtlHours"] = "1",
                ["ISEStudio:Persistence:Provider"] = "sqlite",
                ["ISEStudio:Persistence:SqliteConnection"] = $"Data Source={_sqlitePath}",
                ["ISEStudio:Storage:RdfRoot"] = _rdfRoot,
                ["ISEStudio:Storage:ExportRoot"] = _exportRoot,
            });
        });

        builder.ConfigureServices(services =>
        {
            // Same parser / LLM / storage overrides as AuthTestWebApplicationFactory
            // — SSO only touches /api/auth but Program.cs still wires the
            // ontology, blob, export and chat services at startup.

            var parserDescriptors = services
                .Where(d => d.ServiceType == typeof(IDocumentParser))
                .ToList();
            foreach (var desc in parserDescriptors) services.Remove(desc);
            services.AddSingleton<IDocumentParser, TestDocumentParser>();

            var blobDescriptors = services
                .Where(d => d.ServiceType == typeof(IBlobStore))
                .ToList();
            foreach (var desc in blobDescriptors) services.Remove(desc);
            services.AddSingleton<IBlobStore>(_ => new LocalCasBlobStore(_blobRoot));

            var rdfDescriptors = services
                .Where(d => d.ServiceType.FullName == "ISEStudio.Ontology.StoreWrapper")
                .ToList();
            foreach (var desc in rdfDescriptors) services.Remove(desc);
            services.AddSingleton(typeof(ISEStudio.Ontology.StoreWrapper),
                _ => new ISEStudio.Ontology.StoreWrapper(_rdfRoot));

            services.RemoveAll<IChatClientFactory>();
            services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);

            // Replace JwtBearer's discovery + JWKS fetcher with the in-memory
            // fake so it never hits the network. PostConfigure runs after
            // Program.cs's AddJwtBearer so our values win.
            var fakeHandler = new FakeKeycloakMetadataHandler(Issuer);
            var fakeHttp = new HttpClient(fakeHandler)
            {
                BaseAddress = new Uri(Issuer.Authority),
            };
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    o.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        Issuer.Authority + Issuer.DiscoveryPath,
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(fakeHttp) { RequireHttps = false });
                });
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (File.Exists(_sqlitePath)) File.Delete(_sqlitePath); }
            catch { /* ignore — best effort */ }
            try { if (Directory.Exists(_blobRoot)) Directory.Delete(_blobRoot, recursive: true); }
            catch { /* ignore */ }
            try { if (Directory.Exists(_rdfRoot)) Directory.Delete(_rdfRoot, recursive: true); }
            catch { /* ignore */ }
            try { if (Directory.Exists(_exportRoot)) Directory.Delete(_exportRoot, recursive: true); }
            catch { /* ignore */ }
            foreach (var key in _envVarsToCleanup)
            {
                System.Environment.SetEnvironmentVariable(key, null);
            }
            _envVarsToCleanup.Clear();
        }
        base.Dispose(disposing);
    }

    /// <summary>Answers only the discovery + JWKS URLs, everything else 404s.</summary>
    private sealed class FakeKeycloakMetadataHandler : HttpMessageHandler
    {
        private readonly TestJwtIssuer _issuer;

        public FakeKeycloakMetadataHandler(TestJwtIssuer issuer) => _issuer = issuer;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Match by suffix — the test issuer's Authority includes a realm
            // segment ("/realms/isestudio") so the absolute path of the
            // discovery / JWKS request includes that prefix.
            var path = request.RequestUri!.AbsolutePath;
            System.Console.WriteLine($"[FakeKeycloak] {request.RequestUri}");
            var json = path.EndsWith(_issuer.DiscoveryPath, StringComparison.Ordinal)
                ? _issuer.DiscoveryJson()
                : path.EndsWith(_issuer.JwksPath, StringComparison.Ordinal)
                    ? _issuer.JwksJson()
                    : null;
            return Task.FromResult(new HttpResponseMessage
            {
                StatusCode = json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                Content = new StringContent(json ?? string.Empty,
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}