using System.Net;
using OnToPilot.Tests.Authentication;

namespace OnToPilot.Tests.Authorization;

/// <summary>
/// Verifies the AdminOnly named policy replaces inline
/// <c>[Authorize(Roles="Admin")]</c> without changing observable
/// behavior: admin → 200, non-admin → 403, anonymous → 401. Mirrors
/// the per-test factory pattern used by
/// <see cref="OnToPilot.Tests.Authentication.AuthAdminApiTests"/> —
/// each test gets its own <see cref="AuthTestWebApplicationFactory"/>
/// (and thus its own SQLite database) rather than a shared class
/// fixture, because the factory exposes two public constructors.
/// </summary>
public sealed class AdminPolicyTests
{
    [Theory]
    [InlineData("/api/providers")]
    [InlineData("/api/settings")]
    [InlineData("/api/auth/users")]
    public async Task Anonymous_gets_401(string path)
    {
        await using var app = new AuthTestWebApplicationFactory();
        var client = app.CreateClient();

        var resp = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/providers")]
    [InlineData("/api/settings")]
    [InlineData("/api/auth/users")]
    public async Task Non_admin_user_gets_403(string path)
    {
        await using var app = new AuthTestWebApplicationFactory();
        await app.SeedUserAsync("non-admin");
        var client = app.CreateClient();
        await app.AuthenticateAsAsync(client, "non-admin");

        var resp = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/providers")]
    [InlineData("/api/settings")]
    [InlineData("/api/auth/users")]
    public async Task Admin_user_gets_200(string path)
    {
        await using var app = new AuthTestWebApplicationFactory();
        await app.SeedAdminAsync();
        var client = app.CreateClient();
        await app.AuthenticateAsAsync(client);

        var resp = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
