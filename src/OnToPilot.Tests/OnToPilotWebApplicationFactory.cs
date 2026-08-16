using Microsoft.AspNetCore.Mvc.Testing;

namespace OnToPilot.Tests;

/// <summary>
/// Test host for the OnToPilot web project. Wraps the real ASP.NET Core
/// pipeline so integration tests can <c>CreateClient()</c> against it.
/// </summary>
public sealed class OnToPilotWebApplicationFactory : WebApplicationFactory<Program>
{
}