namespace OnToPilot.Application.Foundation;

/// <summary>
/// One input bundle handed to <see cref="Integration.IIntegrationApiFacade.InvokeAsync"/>.
/// Carries everything a controller method can supply on the wire: the bound
/// knowledge system id (when the route has <c>{ks_id}</c>), the public id
/// (when the route has <c>{public_id}</c>), a sub-resource id (e.g.
/// <c>{document_id}</c>, <c>{token_id}</c>), an arbitrary JSON body for
/// POST/PUT/PATCH, the parsed query string, and the acting user.
///
/// <para>All fields are nullable so a <c>GET /api/health</c>-style call with
/// no inputs is a single <c>new InternalRequest(...)</c> with all-null
/// fields.</para>
/// </summary>
public sealed record InternalRequest(
    long? KnowledgeSystemId,
    string? PublicId,
    string? ResourceId,
    string? SecondResourceId,
    IReadOnlyDictionary<string, object?>? Body,
    IReadOnlyDictionary<string, string?>? Query,
    Actor Actor);