using ModelContextProtocol.Server;

namespace OnToPilot.Mcp;

/// <summary>
/// Prompt surface exposed by the OnToPilot MCP transport. The frozen
/// Python <c>tools/list</c> baseline advertises no prompts, so the .NET
/// server mirrors that surface: <see cref="Inventory"/> is empty and
/// the SDK registration is a no-op (prompts are simply absent from
/// the <c>prompts/list</c> response).
///
/// <para>The class still exists so the <c>WithPrompts&lt;T&gt;()</c>
/// registration in <c>Program.cs</c> resolves a concrete type, and so
/// future prompt additions land in a single, discoverable file.</para>
/// </summary>
[McpServerPromptType]
public sealed class OnToPilotMcpPrompts
{
    /// <summary>
    /// Canonical list of prompts the .NET MCP server advertises. The
    /// parity gate only enforces tool names; prompts are intentionally
    /// out of scope. Keep this empty unless the prompt surface is
    /// added to the frozen Python baseline.
    /// </summary>
    public static IReadOnlyList<McpPromptDescriptor> Inventory() =>
        Array.Empty<McpPromptDescriptor>();
}

/// <summary>
/// One MCP <c>prompts/list</c> entry. Kept as a placeholder record so
/// future prompt additions have a stable shape to extend.
/// </summary>
public sealed record McpPromptDescriptor(
    string Name,
    string Description,
    IReadOnlyList<string> Arguments);