using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OnToPilot.Tests;

/// <summary>
/// Test host for the OnToPilot web project. Wraps the real ASP.NET Core
/// pipeline so integration tests can <c>CreateClient()</c> against it.
/// </summary>
/// <remarks>
/// <para>Sets <see cref="IWebHostBuilder.UseEnvironment(string)"/> to
/// <c>"Testing"</c> so the production-only bootstrap-recovery check (which
/// refuses to start against an empty users table) is skipped, matching the
/// pattern used by <c>AuthTestWebApplicationFactory</c>.</para>
/// </remarks>
public sealed class OnToPilotWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}