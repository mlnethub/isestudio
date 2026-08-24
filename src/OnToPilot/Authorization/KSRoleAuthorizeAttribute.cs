using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Authorization;

/// <summary>
/// Action filter that enforces a minimum <see cref="KSRole"/> on a route
/// bound to a knowledge system. Replaces the 8+ scattered
/// <c>RequireRoleAsync</c> guards inside services with a single
/// declarative attribute — mirrors Python baseline
/// <c>backend/app/permissions.py:52-73</c>'s
/// <c>_require("viewer"/"editor"/"owner")</c> factory.
///
/// <para>Resolution order:</para>
/// <list type="number">
///   <item>Pull <see cref="UserEntity"/> from
///         <c>HttpContext.Items["auth.user"]</c> (set by
///         <see cref="SessionAuthenticationHandler"/>).</item>
///   <item>If <see cref="AllowExternalToken"/> is set and the principal's
///         scheme is <c>ExternalToken</c> / <c>ApiBearer</c>, bypass — those
///         flows are authorized by the token itself, not by KSRole.</item>
///   <item>Extract the KS identifier from <see cref="RouteArgument"/>
///         (<c>id</c> ⇒ <see cref="Guid"/>; <c>publicId</c> ⇒ string lookup).</item>
///   <item>Call <see cref="KnowledgeSystemAccessService.GetEffectiveRoleAsync"/>.</item>
///   <item>Compare to <see cref="Minimum"/>; emit 401 / 403 / 404 envelope.</item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class KSRoleAuthorizeAttribute : Attribute, IAsyncAuthorizationFilter
{
    /// <summary>HttpContext.Items key where <see cref="SessionAuthenticationHandler"/> stashes the user.</summary>
    private const string AuthUserItemKey = "auth.user";

    public KSRole Minimum { get; }

    /// <summary>Route argument name holding the KS identifier. Default <c>"id"</c>.</summary>
    public string RouteArgument { get; init; } = "id";

    /// <summary>
    /// When <c>true</c>, principals authenticated via <c>ExternalToken</c>
    /// or <c>ApiBearer</c> schemes bypass the KSRole check (their scopes
    /// are enforced separately).
    /// </summary>
    public bool AllowExternalToken { get; init; }

    public KSRoleAuthorizeAttribute(KSRole minimum)
    {
        Minimum = minimum;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 1. Pull actor
        if (!context.HttpContext.Items.TryGetValue(AuthUserItemKey, out var raw) || raw is not UserEntity user)
        {
            context.Result = new ObjectResult(new { detail = "Not authenticated" })
                { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        // 2. ExternalToken / ApiBearer bypass
        if (AllowExternalToken)
        {
            var scheme = context.HttpContext.User.Identity?.AuthenticationType;
            if (scheme is ExternalTokenAuthenticationHandler.SchemeName
                       or ApiBearerAuthenticationHandler.SchemeName)
            {
                return;
            }
        }

        // 3. Resolve KS from route
        if (!context.RouteData.Values.TryGetValue(RouteArgument, out var rawId) || rawId is null)
        {
            context.Result = new ObjectResult(new { detail = "Missing knowledge system identifier" })
                { StatusCode = StatusCodes.Status400BadRequest };
            return;
        }

        var services = context.HttpContext.RequestServices;
        var db = services.GetRequiredService<OnToPilotDbContext>();
        var access = services.GetRequiredService<KnowledgeSystemAccessService>();

        KnowledgeSystemEntity? ks;
        if (rawId is Guid ksGuid)
        {
            ks = await db.KnowledgeSystems.FirstOrDefaultAsync(k => k.Id == ksGuid);
        }
        else if (rawId is string publicId)
        {
            // Route values are always strings, even for {id:guid} constraints,
            // so a string that parses as a Guid is resolved by Id; anything
            // else is a publicId. PublicId lookup runs first because a
            // publicId itself is Guid-shaped (hex without dashes).
            ks = await db.KnowledgeSystems.FirstOrDefaultAsync(k => k.PublicId == publicId);
            if (ks is null && Guid.TryParse(publicId, out var asGuid))
            {
                ks = await db.KnowledgeSystems.FirstOrDefaultAsync(k => k.Id == asGuid);
            }
        }
        else
        {
            context.Result = new ObjectResult(new { detail = "Unsupported knowledge system identifier type" })
                { StatusCode = StatusCodes.Status400BadRequest };
            return;
        }

        // 4. 404 if KS not found
        if (ks is null)
        {
            context.Result = new ObjectResult(new { detail = "Knowledge system not found" })
                { StatusCode = StatusCodes.Status404NotFound };
            return;
        }

        // 5. Role check
        var role = await access.GetEffectiveRoleAsync(user, ks, db, context.HttpContext.RequestAborted);
        if (role == KSRole.None)
        {
            context.Result = new ObjectResult(new { detail = "You don't have access to this knowledge system" })
                { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
        if (role < Minimum)
        {
            context.Result = new ObjectResult(new { detail = "Insufficient permissions" })
                { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
    }
}
