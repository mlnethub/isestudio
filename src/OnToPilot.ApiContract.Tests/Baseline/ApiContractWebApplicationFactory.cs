using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OnToPilot.ApiContract.Tests.Baseline;

/// <summary>
/// Lightweight test host for the OnToPilot web project used by the
/// contract inventory tests. Sets the environment to <c>"Testing"</c> so
/// the bootstrap-recovery check (which refuses to start against an
/// empty users table) is skipped — the contract tests do not need a
/// seeded database.
/// </summary>
internal sealed class ApiContractWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}