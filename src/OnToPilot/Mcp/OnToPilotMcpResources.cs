using ModelContextProtocol.Server;

namespace OnToPilot.Mcp;

/// <summary>
/// Resource surface exposed by the OnToPilot MCP transport. The frozen
/// Python <c>tools/list</c> baseline advertises no resources, so the
/// .NET server mirrors that surface: <see cref="Inventory"/> is empty
/// and the SDK registration is a no-op (resources are simply absent
/// from the <c>resources/list</c> response).
///
/// <para>The class still exists so the <c>WithResources&lt;T&gt;()</c>
/// registration in <c>Program.cs</c> resolves a concrete type, and so
/// future resource additions land in a single, discoverable file.</para>
/// </summary>
[McpServerResourceType]
public sealed class OnToPilotMcpResources
{
    /// <summary>
    /// Canonical list of resources the .NET MCP server advertises. The
    /// parity gate only enforces tool names; resources are intentionally
    /// out of scope. Keep this empty unless the resource surface is
    /// added to the frozen Python baseline.
    /// </summary>
    public static IReadOnlyList<McpResourceDescriptor> Inventory() =>
        Array.Empty<McpResourceDescriptor>();
}

/// <summary>
/// One MCP <c>resources/list</c> entry. Kept as a placeholder record
/// so future resource additions have a stable shape to extend.
/// </summary>
public sealed record McpResourceDescriptor(
    string Name,
    string Description,
    string UriTemplate,
    IReadOnlyList<string> RequiredScopes);