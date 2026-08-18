using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

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
/// </summary>
internal sealed class ApiContractWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestSchemeName = "ContractTest";

    private readonly string _sqlitePath = Path.Combine(
        Path.GetTempPath(),
        $"ontopilot-contract-{Guid.NewGuid():N}.db");

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
        });
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (File.Exists(_sqlitePath)) File.Delete(_sqlitePath); }
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
        var admin = new UserEntity
        {
            Id = Guid.Empty,
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