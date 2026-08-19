using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OnToPilot.Authentication;
using OnToPilot.Documents;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Llm;
using OnToPilot.Parsing;
using OnToPilot.Storage;
using OnToPilot.Tests.Documents;
using OnToPilot.Tests.Extraction;

namespace OnToPilot.Tests.Authentication;

/// <summary>
/// Web host used by the auth/access contract tests. Flips the persistence
/// provider to SQLite via configuration and gives each factory instance its
/// own unique on-disk SQLite database (deleted on dispose) so EF Core opens
/// a fresh connection without leaking providers across tests.
/// </summary>
public class AuthTestWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "admin12345strong";
    public const string AdminDisplayName = "Test Admin";
    public const string OtherUsername = "alice";
    public const string OtherPassword = "alice12345strong";

    private readonly string _sqlitePath;
    private readonly string _blobRoot;
    private readonly string _rdfRoot;
    private readonly IPasswordService? _passwordOverride;

    public AuthTestWebApplicationFactory() : this(passwordOverride: null)
    {
    }

    /// <summary>
    /// Construct with a custom <see cref="IPasswordService"/>. Used by the
    /// timing-safe login regression test to assert the controller calls
    /// <c>Verify</c> on every login attempt (so missing vs wrong password
    /// take the same time).
    /// </summary>
    public AuthTestWebApplicationFactory(IPasswordService? passwordOverride)
    {
        var testId = Guid.NewGuid().ToString("N");
        var rawPath = Path.Combine(
            Path.GetTempPath(),
            $"ontopilot-auth-tests-{testId}.db");
        // SQLite connection-string parser is sensitive to backslashes.
        _sqlitePath = rawPath.Replace('\\', '/');
        _blobRoot = Path.Combine(
            Path.GetTempPath(),
            $"ontopilot-blob-{testId}");
        // Per-test Oxigraph path so the singleton StoreWrapper doesn't
        // share the on-disk store between parallel tests. Oxigraph's
        // Store ctor refuses backslashes on Windows — normalise here
        // so the production wiring (with forward slashes) and the test
        // wiring share the same happy-path.
        _rdfRoot = Path.Combine(
            Path.GetTempPath(),
            $"ontopilot-rdf-{testId}").Replace('\\', '/');
        _passwordOverride = passwordOverride;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OnToPilot:CookieSecure"] = "false",
                ["OnToPilot:SessionTtlHours"] = "1",
                ["OnToPilot:Persistence:Provider"] = "sqlite",
                // Use the explicit "Data Source=" form so the SQLite
                // connection-string parser doesn't choke on the raw path.
                ["OnToPilot:Persistence:SqliteConnection"] = $"Data Source={_sqlitePath}",
            });
        });

        builder.ConfigureServices(services =>
        {
            if (_passwordOverride is not null)
            {
                // Replace the production IPasswordService registration with
                // the spy/fake the test injected.
                var existing = services.Where(d => d.ServiceType == typeof(IPasswordService)).ToList();
                foreach (var desc in existing) services.Remove(desc);
                services.AddSingleton(_passwordOverride);
            }

            // Replace the production document parser with the test fake
            // so parse contract tests don't depend on DoclingDotNet /
            // PdfPig native binaries.
            var parserDescriptors = services
                .Where(d => d.ServiceType == typeof(IDocumentParser))
                .ToList();
            foreach (var desc in parserDescriptors) services.Remove(desc);
            services.AddSingleton<IDocumentParser, TestDocumentParser>();

            // Per-test isolated blob root so concurrent tests don't share
            // disk state and orphans accumulate.
            var blobDescriptors = services
                .Where(d => d.ServiceType == typeof(IBlobStore))
                .ToList();
            foreach (var desc in blobDescriptors) services.Remove(desc);
            services.AddSingleton<IBlobStore>(_ => new LocalCasBlobStore(_blobRoot));

            // Per-test Oxigraph handle so the ontology + impact tests
            // don't share RDF state with each other (writes are land-locked
            // to the on-disk store). Use the fully-qualified ServiceType
            // because StoreWrapper is registered in OnToPilot.Ontology and
            // we don't want to widen the test host's namespace imports.
            var rdfDescriptors = services
                .Where(d => d.ServiceType.FullName == "OnToPilot.Ontology.StoreWrapper")
                .ToList();
            foreach (var desc in rdfDescriptors) services.Remove(desc);
            var rdfPath = _rdfRoot;
            services.AddSingleton(typeof(OnToPilot.Ontology.StoreWrapper),
                _ => new OnToPilot.Ontology.StoreWrapper(rdfPath));

            // B6b: override production IChatClientFactory with the shared test fake
            // so all extraction tests drive the orchestrator through FakeChat.
            services.RemoveAll<IChatClientFactory>();
            services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
        });
    }

    /// <summary>
    /// Builds the host (if needed) and returns a fresh
    /// <see cref="OnToPilotDbContext"/> against this factory's SQLite
    /// database. Ensures the schema exists.
    /// </summary>
    public OnToPilotDbContext CreateDbContext()
    {
        _ = CreateClient();
        var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OnToPilotDbContext>();
        db.Database.EnsureCreated();
        return db;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (File.Exists(_sqlitePath)) File.Delete(_sqlitePath); }
            catch { /* ignore — best effort */ }
            try { if (Directory.Exists(_blobRoot)) Directory.Delete(_blobRoot, recursive: true); }
            catch { /* ignore — best effort */ }
            try { if (Directory.Exists(_rdfRoot)) Directory.Delete(_rdfRoot, recursive: true); }
            catch { /* ignore — best effort */ }
        }
        base.Dispose(disposing);
    }
}