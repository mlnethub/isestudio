using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-service seam for the seven token dispatcher arms
/// (12/13 slice): <c>tokens.list</c> / <c>tokens.create</c> /
/// <c>tokens.revoke</c> / <c>tokens.reveal</c> plus
/// <c>mcp_tokens.list</c> / <c>mcp_tokens.create</c> /
/// <c>mcp_tokens.revoke</c>. The implementation resolves the scoped
/// <c>TokenManagementService</c> through the constructor and owns
/// envelope unpacking (body DTOs, KnowledgeSystemGuid, ResourceId
/// token Guid) + the snake_case wire projections.
///
/// <para>Returns are <c>object?</c> because the wire DTOs
/// (<c>TokenOut</c> / <c>TokenCreatedOut</c> / <c>McpTokenOut</c> /
/// <c>McpTokenCreatedOut</c>) live in the Infrastructure slice. A
/// <c>null</c> return degrades to the dispatcher's schema-compatible
/// fallback per arm; a missing body throws
/// <see cref="InvalidOperationException"/> exactly like the pre-split
/// helpers did.</para>
/// </summary>
public interface ITokenApplicationService
{
    /// <summary><c>tokens.list</c> — API tokens of the KS.</summary>
    Task<object?> ListTokensAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>tokens.create</c> — body <c>{name, scopes, ...}</c>.</summary>
    Task<object?> CreateTokenAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>tokens.revoke</c> — token Guid in <c>ResourceId</c>.</summary>
    Task<object?> RevokeTokenAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>tokens.reveal</c> — token Guid in <c>ResourceId</c>, plaintext once.</summary>
    Task<object?> RevealTokenAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>mcp_tokens.list</c> — MCP tokens of the KS for the actor.</summary>
    Task<object?> ListMcpTokensAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>mcp_tokens.create</c> — body <c>{name, ...}</c>.</summary>
    Task<object?> CreateMcpTokenAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>mcp_tokens.revoke</c> — token Guid in <c>ResourceId</c>.</summary>
    Task<object?> RevokeMcpTokenAsync(InternalRequest request, CancellationToken cancellationToken);
}
