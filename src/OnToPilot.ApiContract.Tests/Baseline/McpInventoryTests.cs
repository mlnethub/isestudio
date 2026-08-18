using OnToPilot.Mcp;

namespace OnToPilot.ApiContract.Tests.Baseline;

/// <summary>
/// Equality that compares the MCP tool inventory by name only. The
/// description strings live in two places (Python source vs .NET
/// <see cref="OnToPilotMcpTools"/>) and are allowed to drift in
/// wording; the parity gate enforces name equality so a tool that
/// appears in the baseline is exposed by the .NET server and vice
/// versa.
/// </summary>
internal sealed class McpToolNameComparer : IEqualityComparer<McpToolDescriptor>
{
    public static readonly McpToolNameComparer Instance = new();
    public bool Equals(McpToolDescriptor? x, McpToolDescriptor? y)
    {
        if (x is null || y is null) return x is null && y is null;
        return string.Equals(x.Name, y.Name, StringComparison.Ordinal);
    }
    public int GetHashCode(McpToolDescriptor obj)
        => HashCode.Combine(obj.Name);
}

/// <summary>
/// Gate that proves the .NET MCP transport advertises exactly the same
/// <c>tools/list</c> names as the frozen Python baseline. The diff
/// must be empty in both directions once task 4 lands.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class McpInventoryTests
{
    /// <summary>
    /// Exact verbatim name required by the api-mcp plan. Once task 4
    /// wires the transport and the inventory returns all 20 baseline
    /// names, the diff between expected and actual is empty.
    /// </summary>
    [Fact]
    public void Mcp_tools_match_baseline()
    {
        var expectedNames = BaselineLoader.McpTools()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var actualNames = OnToPilotMcpTools.Inventory()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var missingInDotNet = expectedNames.Except(actualNames, StringComparer.Ordinal).ToArray();
        var extraInDotNet = actualNames.Except(expectedNames, StringComparer.Ordinal).ToArray();

        var detail = BuildDiffReport(missingInDotNet, extraInDotNet);
        Assert.True(
            missingInDotNet.Length == 0 && extraInDotNet.Length == 0,
            $"MCP tool inventory drift between Python baseline and .NET app.\n{detail}");
    }

    private static string BuildDiffReport(
        IReadOnlyList<string> missingInDotNet,
        IReadOnlyList<string> extraInDotNet)
    {
        var builder = new System.Text.StringBuilder();
        if (missingInDotNet.Count > 0)
        {
            builder.AppendLine($"Tools present in Python baseline but missing in .NET ({missingInDotNet.Count}):");
            foreach (var tool in missingInDotNet)
            {
                builder.AppendLine($"  - {tool}");
            }
        }
        if (extraInDotNet.Count > 0)
        {
            builder.AppendLine($"Tools present in .NET but missing in Python baseline ({extraInDotNet.Count}):");
            foreach (var tool in extraInDotNet)
            {
                builder.AppendLine($"  + {tool}");
            }
        }
        return builder.ToString();
    }
}