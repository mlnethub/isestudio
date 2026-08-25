using ModelContextProtocol;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Mcp;

/// <summary>
/// Snapshot of the authenticated principal the MCP transport resolves on
/// every request. The token row itself is intentionally NOT cached
/// anywhere on this record — the role is read live from
/// <see cref="KnowledgeSystemEntity.OwnerId"/>, the user's
/// <see cref="UserEntity.Active"/> flag, and the matching
/// <see cref="KSGrantEntity"/> row on every MCP tool call so a membership
/// downgrade takes effect on the next request without invalidating the
/// bearer token. See <see cref="McpPrincipalAccessor"/> for the
/// real-time lookup path.
/// </summary>
public sealed record McpPrincipal(
    UserEntity User,
    KnowledgeSystemEntity KnowledgeSystem,
    IReadOnlyList<string> Scopes);

/// <summary>
/// Container of last-resort state a tool can throw to surface a 401/403
/// envelope. The MCP transport uses <see cref="McpToolException"/> on the
/// JSON-RPC path so the SDK returns the structured error code to the
/// caller; the SDK wires the message into the wire-protocol
/// <c>isError</c> response.
///
/// <para>The exception derives from <see cref="McpException"/> rather
/// than <see cref="Exception"/> so the SDK's tool-call pipeline
/// preserves <see cref="Exception.Message"/> on the wire. Per the SDK
/// source, only <see cref="McpException"/>-derived exceptions are
/// surfaced with their message attached; any other exception type is
/// replaced with a generic <c>"An error occurred invoking 'name'."</c>
/// envelope, which would hide the role / scope wording the contract
/// pins.</para>
/// </summary>
public sealed class McpToolException : McpException
{
    /// <summary>Create a new <see cref="McpToolException"/> with the supplied detail.</summary>
    public McpToolException(string message) : base(message) { }

    /// <summary>Create a new <see cref="McpToolException"/> wrapping an inner exception.</summary>
    public McpToolException(string message, Exception inner) : base(message, inner) { }
}