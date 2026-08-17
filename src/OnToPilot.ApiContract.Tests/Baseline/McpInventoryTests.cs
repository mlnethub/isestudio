namespace OnToPilot.ApiContract.Tests.Baseline;

/// <summary>
/// Gate that proves the .NET MCP transport advertises exactly the same
/// <c>tools/list</c> surface as the frozen Python baseline. The diff
/// must be empty in both directions once task 4 lands.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class McpInventoryTests
{
    /// <summary>
    /// Exact verbatim name required by the api-mcp plan. Currently
    /// expected to fail with a clear diff (20 tools expected, 0 found)
    /// until task 4 wires the MCP transport.
    /// </summary>
    [Fact]
    public void Mcp_tools_match_baseline()
    {
        var expected = BaselineLoader.McpTools();
        var actual = OnToPilotMcpTools.Inventory();

        var missingInDotNet = expected.Except(actual).ToArray();
        var extraInDotNet = actual.Except(expected).ToArray();

        var detail = BuildDiffReport(missingInDotNet, extraInDotNet);
        Assert.True(
            missingInDotNet.Length == 0 && extraInDotNet.Length == 0,
            $"MCP tool inventory drift between Python baseline and .NET app.\n{detail}");
    }

    private static string BuildDiffReport(
        IReadOnlyList<McpTool> missingInDotNet,
        IReadOnlyList<McpTool> extraInDotNet)
    {
        var builder = new System.Text.StringBuilder();
        if (missingInDotNet.Count > 0)
        {
            builder.AppendLine($"Tools present in Python baseline but missing in .NET ({missingInDotNet.Count}):");
            foreach (var tool in missingInDotNet)
            {
                builder.AppendLine($"  - {tool.Name}");
            }
        }
        if (extraInDotNet.Count > 0)
        {
            builder.AppendLine($"Tools present in .NET but missing in Python baseline ({extraInDotNet.Count}):");
            foreach (var tool in extraInDotNet)
            {
                builder.AppendLine($"  + {tool.Name}");
            }
        }
        return builder.ToString();
    }
}