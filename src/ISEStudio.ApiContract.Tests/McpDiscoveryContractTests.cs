using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Mcp;

namespace ISEStudio.ApiContract.Tests;

/// <summary>
/// Discovery parity tests for the ISEStudio MCP transport. The Python
/// backend's <c>tools/list</c> payload is the frozen baseline; the
/// .NET inventory must surface the same 20 tool names. The discovery
/// gate does not exercise the wire layer — it directly compares
/// <see cref="ISEStudioMcpTools.Inventory"/> against the JSON baseline
/// so a regression in either side surfaces as a clear diff in the
/// test runner output.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class McpDiscoveryContractTests
{
    /// <summary>
    /// Verifies the .NET inventory returns exactly 20 tools — matching
    /// the size of the frozen Python baseline. A regression that drops
    /// or adds a tool surfaces as a clear count mismatch.
    /// </summary>
    [Fact]
    public void Tools_list_returns_20_tools()
    {
        var inventory = ISEStudioMcpTools.Inventory();
        Assert.Equal(20, inventory.Count);
    }

    /// <summary>
    /// Verifies the inventory contains every Python-baseline name. The
    /// <c>McpInventoryTests</c> gate is the canonical equality check;
    /// this test re-asserts the property from the discovery angle so a
    /// future refactor that splits the gate into two files still
    /// surfaces the missing-name diff.
    /// </summary>
    [Fact]
    public void Discovery_matches_baseline()
    {
        var expected = Baseline.BaselineLoader.McpTools()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        var actual = ISEStudioMcpTools.Inventory()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = expected.Except(actual, StringComparer.Ordinal).OrderBy(n => n).ToArray();
        var extra = actual.Except(expected, StringComparer.Ordinal).OrderBy(n => n).ToArray();

        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            "MCP discovery drift.\nMissing in .NET: " + string.Join(", ", missing)
                + "\nExtra in .NET: " + string.Join(", ", extra));
    }

    /// <summary>
    /// Sanity check: every tool has a non-empty description. The
    /// brief mandates that the inventory shape mirrors the Python
    /// baseline, and the Python baseline carries a description for
    /// every tool. An empty description is a regression the LLM
    /// client will notice immediately.
    /// </summary>
    [Fact]
    public void Every_tool_has_non_empty_description()
    {
        foreach (var tool in ISEStudioMcpTools.Inventory())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description),
                $"Tool '{tool.Name}' has an empty description.");
        }
    }

    /// <summary>
    /// Sanity check: destructive tools advertise a scope list. The
    /// Python baseline does not pin scopes per-tool, but the .NET
    /// transport enforces them on every call so an empty scope list on
    /// a destructive tool would let any caller invoke it.
    /// </summary>
    [Fact]
    public void Destructive_tools_advertise_required_scopes()
    {
        var destructiveNames = new[]
        {
            "apply_instance_change",
            "apply_ontology_changes",
            "apply_vocabulary_change",
            "decide_review_item",
            "manage_release",
            "rollback_history_event",
        };
        var inventory = ISEStudioMcpTools.Inventory()
            .ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var name in destructiveNames)
        {
            Assert.True(inventory.TryGetValue(name, out var tool),
                $"Destructive tool '{name}' is missing from the inventory.");
            Assert.NotEmpty(tool!.RequiredScopes);
        }
    }
}