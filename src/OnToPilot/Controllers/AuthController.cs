using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Foundation;
using OnToPilot.Application.Integration;

namespace OnToPilot.Controllers;

/// <summary>
/// Internal <c>/api/auth</c> surface &mdash; matches the Python backend's
/// <c>backend/app/api/auth.py</c> shape so existing tooling keeps working
/// during the .NET migration.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IIntegrationApiFacade _facade;

    public AuthController(IIntegrationApiFacade facade)
    {
        _facade = facade;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public Task<IActionResult> LoginAsync([FromBody] object body, CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync("auth.login", Build(body, ct: ct), ct));

    [HttpPost("logout")]
    [Authorize]
    public Task<IActionResult> LogoutAsync(CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync("auth.logout", new InternalRequest(null, null, null, null, null, null, Actor), ct));

    [HttpGet("me")]
    [Authorize]
    public Task<IActionResult> MeAsync(CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync("auth.me", new InternalRequest(null, null, null, null, null, null, Actor), ct));

    [HttpPatch("me")]
    [Authorize]
    public Task<IActionResult> UpdateMeAsync([FromBody] object body, CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync("auth.update_me", Build(body, ct: ct), ct));

    [HttpGet("users")]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> ListUsersAsync(CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync("auth.list_users", new InternalRequest(null, null, null, null, null, null, Actor), ct));

    [HttpPost("users")]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> CreateUserAsync([FromBody] object body, CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync("auth.create_user", Build(body, ct: ct), ct));

    [HttpDelete("users/{uid}")]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> DeleteUserAsync(string uid, CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync("auth.delete_user", new InternalRequest(null, null, uid, null, null, null, Actor), ct));

    [HttpPatch("users/{uid}")]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> UpdateUserAsync(string uid, [FromBody] object body, CancellationToken ct)
        => OkEnvelope(_facade.InvokeAsync("auth.update_user", new InternalRequest(null, null, uid, null, ToBody(body), null, Actor), ct));

    private Actor Actor =>
        HttpContext.Items.TryGetValue("auth.user", out var v) && v is not null
            ? new Actor("system")
            : new Actor("anonymous");

    private InternalRequest Build(object? body, long? ks = null, string? pub = null, string? res = null, string? res2 = null, CancellationToken ct = default)
        => new(ks, pub, res, res2, ToBody(body), null, Actor);

    private static IReadOnlyDictionary<string, object?>? ToBody(object? body)
    {
        if (body is null) return null;
        if (body is IReadOnlyDictionary<string, object?> d) return d;
        // For anonymous / loose objects we just pass a wrapping dict keyed
        // by "_" so the dispatcher can decide how to consume the body.
        return new Dictionary<string, object?> { ["_"] = body };
    }

    private async Task<IActionResult> OkEnvelope(Task<object?> call)
    {
        var result = await call.ConfigureAwait(false);
        return result is null ? Ok(new { ok = true }) : Ok(result);
    }
}