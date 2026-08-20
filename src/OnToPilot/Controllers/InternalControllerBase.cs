using Microsoft.AspNetCore.Mvc;
using OnToPilot.Application.Foundation;
using OnToPilot.Application.Integration;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Controllers;

/// <summary>
/// Shared scaffolding every internal controller inherits. Centralises
/// the four knobs the OpenAPI inventory reads (route → HTTP verb, facade
/// dispatch, body unpacking, actor resolution) so individual controllers
/// stay one-method-per-operation thin.
/// </summary>
public abstract class InternalControllerBase : ControllerBase
{
    private readonly IIntegrationApiFacade _facade;

    protected InternalControllerBase(IIntegrationApiFacade facade)
    {
        _facade = facade;
    }

    /// <summary>The facade shared by all internal operations.</summary>
    protected IIntegrationApiFacade Facade => _facade;

    /// <summary>
    /// Build a request envelope for an operation that takes no body. The
    /// caller passes the resource ids and the framework fills the actor
    /// from the session.
    /// </summary>
    protected InternalRequest Req(long? ks = null, string? pub = null, string? res = null, string? res2 = null)
        => new(ks, pub, res, res2, null, QueryMap(), ResolveActor());

    /// <summary>
    /// Build a request envelope with an opaque JSON body. The controller
    /// doesn't need to inspect the shape &mdash; the dispatcher decides.
    /// </summary>
    protected InternalRequest ReqWithBody(object? body, long? ks = null, string? pub = null, string? res = null, string? res2 = null)
        => new(ks, pub, res, res2, ToBody(body), QueryMap(), ResolveActor());

    /// <summary>
    /// Build a request envelope for a slice whose route already binds the
    /// knowledge-system id as a PK <c>Guid</c> (<c>{id:guid}</c>). Carries
    /// the Guid in <see cref="InternalRequest.KnowledgeSystemGuid"/> so the
    /// dispatcher forwards it directly to the migrated service signatures.
    /// </summary>
    protected InternalRequest ReqGuid(Guid ks, string? pub = null, string? res = null, string? res2 = null)
        => new(null, pub, res, res2, null, QueryMap(), ResolveActor(), ks);

    /// <summary>
    /// Body-carrying variant of <see cref="ReqGuid"/>.
    /// </summary>
    protected InternalRequest ReqGuidWithBody(object? body, Guid ks, string? pub = null, string? res = null, string? res2 = null)
        => new(null, pub, res, res2, ToBody(body), QueryMap(), ResolveActor(), ks);

    /// <summary>Dispatch the named operation and wrap the result in <c>Ok(...)</c>.</summary>
    protected async Task<IActionResult> InvokeAsync(string operation, InternalRequest request, CancellationToken ct)
    {
        var payload = await _facade.InvokeAsync(operation, request, ct).ConfigureAwait(false);
        return payload is null ? Ok(new { ok = true }) : Ok(payload);
    }

    /// <summary>
    /// Subclasses (e.g. <see cref="DocumentsController"/>) need access
    /// to the actor when they bypass the facade for operations whose
    /// request body is not JSON (multipart/form-data file upload).
    /// </summary>
    protected Actor ResolveActor()
    {
        // Pull the authenticated user off the request scope; the auth
        // handlers stash the UserEntity under the SessionAuthenticationHandler
        // key. We hand the actor the user's Guid so downstream services can
        // resolve the full row via a primary-key lookup.
        if (HttpContext.Items.TryGetValue("auth.user", out var raw) && raw is not null)
        {
            if (raw is OnToPilot.Infrastructure.Persistence.Entities.UserEntity user)
            {
                return new Actor(user.Id.ToString(), user.DisplayName ?? user.Username);
            }
            return new Actor(raw.ToString() ?? "system");
        }
        return new Actor("anonymous");
    }

    private IReadOnlyDictionary<string, string?>? QueryMap()
    {
        if (Request.Query is null || Request.Query.Count == 0) return null;
        var dict = new Dictionary<string, string?>(Request.Query.Count);
        foreach (var kv in Request.Query)
        {
            dict[kv.Key] = kv.Value.ToString();
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, object?>? ToBody(object? body)
    {
        if (body is null) return null;
        if (body is IReadOnlyDictionary<string, object?> d) return d;
        return new Dictionary<string, object?> { ["_"] = body };
    }
}