using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Application.Integration;
using OnToPilot.Mcp;

namespace OnToPilot.ApiContract.Tests;

/// <summary>
/// Behavior tests for the OnToPilot MCP transport. These exercise
/// the tool bodies directly (without going through HTTP / JSON-RPC)
/// so a regression in <see cref="OnToPilotMcpTools"/> fails with a
/// clear error message rather than a confusing wire-envelope error.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class McpBehaviorTests
{
    /// <summary>
    /// Verifies the inventory size is exactly 20. A regression that
    /// drops a tool or duplicates one surfaces here first.
    /// </summary>
    [Fact]
    public void Inventory_size_is_20()
    {
        Assert.Equal(20, OnToPilotMcpTools.Inventory().Count);
    }

    /// <summary>
    /// Every tool declares at least one scope. Empty scopes would let
    /// any authenticated caller invoke any tool, which the brief
    /// forbids.
    /// </summary>
    [Fact]
    public void Every_tool_advertises_at_least_one_scope()
    {
        foreach (var tool in OnToPilotMcpTools.Inventory())
        {
            Assert.NotEmpty(tool.RequiredScopes);
        }
    }

    /// <summary>
    /// The destructive cap is 50 edits. The cap constant is part of
    /// the public surface so future callers can reference it; a
    /// regression here means the cap silently drifts.
    /// </summary>
    [Fact]
    public void Destructive_cap_is_50_edits()
    {
        Assert.Equal(50, OnToPilotMcpTools.MaxEditsPerDestructiveCall);
    }

    /// <summary>
    /// The destructive payload size cap is 200 KiB. Same rationale as
    /// the edit cap: lock the constant so the brief-mandated limit
    /// cannot drift.
    /// </summary>
    [Fact]
    public void Destructive_payload_cap_is_200_kib()
    {
        Assert.Equal(200 * 1024, OnToPilotMcpTools.MaxDestructivePayloadBytes);
    }

    /// <summary>
    /// Preview must not mutate state. The dispatcher placeholder
    /// returns an empty preview (no edits applied), but the contract
    /// is that the preview tool method exists and round-trips through
    /// <see cref="IIntegrationApiFacade.PreviewOntologyChangesAsync"/>
    /// without calling the destructive path. We assert the public
    /// surface compiles and the inventory advertises the tool so a
    /// future removal breaks the contract test.
    /// </summary>
    [Fact]
    public void Preview_tool_is_exposed()
    {
        var names = OnToPilotMcpTools.Inventory()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("preview_ontology_changes", names);
    }

    /// <summary>
    /// The destructive tool family advertises the confirm flag as a
    /// boolean parameter — surfaced by the SDK from each method's
    /// argument list. We assert the inventory lists every destructive
    /// tool with at least one scope so the LLM client knows to gate
    /// the call.
    /// </summary>
    [Fact]
    public void Destructive_tools_listed_in_inventory()
    {
        var destructiveNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "apply_instance_change",
            "apply_ontology_changes",
            "apply_vocabulary_change",
            "decide_review_item",
            "manage_release",
            "rollback_history_event",
        };
        var inventory = OnToPilotMcpTools.Inventory()
            .ToDictionary(t => t.Name, t => t, StringComparer.Ordinal);
        foreach (var name in destructiveNames)
        {
            Assert.True(inventory.ContainsKey(name), $"Destructive tool '{name}' missing from inventory.");
        }
    }
}