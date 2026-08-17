namespace OnToPilot.ApiContract.Tests.Baseline;

/// <summary>
/// Inventory of the MCP <c>tools/list</c> surface as exposed by the
/// .NET MCP transport. Task 4 owns the real implementation; for task 1
/// the inventory is intentionally empty so the parity test fails with a
/// clear diff (20 tools expected, 0 found).
/// </summary>
public static class OnToPilotMcpTools
{
    /// <summary>
    /// Return the tools the .NET MCP server will advertise. The shape
    /// matches the Python baseline (sorted by name, with the required
    /// scope list empty because the Python baseline does not carry
    /// scopes either).
    /// </summary>
    public static IReadOnlyList<McpTool> Inventory() => Array.Empty<McpTool>();
}