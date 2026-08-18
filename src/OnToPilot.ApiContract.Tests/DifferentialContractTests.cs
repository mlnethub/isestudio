using System.Text.Json;
using OnToPilot.ApiContract.Tests.Differential;

namespace OnToPilot.ApiContract.Tests;

/// <summary>
/// Verifies the differential contract <see cref="Normalizer"/> used by
/// <c>migration/scripts/Invoke-ContractComparison.ps1</c>. The runner fires
/// the same scenario at the Python and .NET backends, captures the status
/// code, the subset of headers declared in <c>compareHeaders</c>, and the
/// response body. Before diffing the two payloads the runner normalises
/// each body by removing the dynamic fields listed in
/// <c>migration/contracts/normalization.json</c>.
///
/// <para>These tests pin down the normaliser's contract so the runner's
/// diff stays a pure comparison of business fields. Every test is
/// tagged <see cref="ApiContractCategoryAttribute"/> so the gate filter
/// (<c>dotnet test --filter Category=ApiContract</c>) picks them up
/// alongside the inventory tests.</para>
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class DifferentialContractTests
{
    private const string DefaultAllowlist = "created_at,updated_at,trace_id,token,*_token";

    /// <summary>
    /// Verbatim name and body required by the Stage 5 plan. Locks down
    /// the three properties every other test in this file relies on:
    /// <list type="bullet">
    ///   <item><c>id</c> survives untouched.</item>
    ///   <item><c>name</c> survives untouched (business field).</item>
    ///   <item><c>created_at</c> is removed because the allowlist includes
    ///         it as a timestamp-style dynamic field.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Normalizer_only_removes_allowlisted_dynamic_fields()
    {
        var normalized = Normalizer.Apply(
            """{"id":7,"created_at":"now","name":"Pump"}""",
            DefaultAllowlist);

        Assert.Equal(7, normalized.GetProperty("id").GetInt32());
        Assert.Equal("Pump", normalized.GetProperty("name").GetString());
        Assert.False(normalized.TryGetProperty("created_at", out _));
    }

    /// <summary>
    /// Sibling test: a nested business object (the <c>owner</c> field
    /// carries its own <c>id</c> and <c>name</c>) keeps every nested
    /// property except the allowlisted <c>created_at</c> at every depth.
    /// </summary>
    [Fact]
    public void Normalizer_preserves_nested_business_fields()
    {
        const string payload = """
            {
              "id": 1,
              "created_at": "2026-01-01T00:00:00Z",
              "owner": {
                "id": 42,
                "name": "alice",
                "created_at": "2026-01-02T00:00:00Z"
              }
            }
            """;

        var normalized = Normalizer.Apply(payload, DefaultAllowlist);

        Assert.Equal(1, normalized.GetProperty("id").GetInt32());
        var owner = normalized.GetProperty("owner");
        Assert.Equal(42, owner.GetProperty("id").GetInt32());
        Assert.Equal("alice", owner.GetProperty("name").GetString());
        Assert.False(owner.TryGetProperty("created_at", out _));
        Assert.False(normalized.TryGetProperty("created_at", out _));
    }

    /// <summary>
    /// Sibling test: the allowlist is recursive and pattern-aware. A
    /// <c>*_token</c> wildcard must match every key ending with
    /// <c>_token</c> (e.g. <c>access_token</c>, <c>refresh_token</c>,
    /// <c>trace_token</c>) anywhere in the document, including array
    /// entries. Non-matching keys (<c>tokenizer</c>, <c>token_kind</c>)
    /// must survive.
    /// </summary>
    [Fact]
    public void Normalizer_recursively_strips_allowlisted_keys()
    {
        const string payload = """
            {
              "tokenizer": "bert",
              "access_token": "abc",
              "session": {
                "refresh_token": "xyz",
                "trace_id": "01H..."
              },
              "history": [
                { "token_kind": "bearer", "trace_token": "deadbeef" }
              ]
            }
            """;

        var normalized = Normalizer.Apply(payload, DefaultAllowlist);

        // Non-matching business keys survive.
        Assert.Equal("bert", normalized.GetProperty("tokenizer").GetString());
        Assert.False(normalized.TryGetProperty("access_token", out _));

        var session = normalized.GetProperty("session");
        Assert.False(session.TryGetProperty("refresh_token", out _));
        Assert.False(session.TryGetProperty("trace_id", out _));

        var history = normalized.GetProperty("history");
        var entry = history[0];
        Assert.Equal("bearer", entry.GetProperty("token_kind").GetString());
        Assert.False(entry.TryGetProperty("trace_token", out _));
    }

    /// <summary>
    /// Verifies the body diff the runner relies on: two payloads that
    /// only differ in their dynamic fields must compare equal after
    /// normalisation. This is the actual contract <c>Invoke-ContractComparison.ps1</c>
    /// depends on — if this stops being true the runner's "no
    /// unapproved differences" verdict becomes meaningless.
    /// </summary>
    [Fact]
    public void Normalizer_makes_dynamic_only_differences_disappear()
    {
        var pythonBody = """
            {"id":7,"name":"Pump","created_at":"2026-01-01T00:00:00Z","trace_id":"01HAAA"}
            """;
        var dotnetBody = """
            {"id":7,"name":"Pump","created_at":"2026-01-02T12:34:56Z","trace_id":"01HBBB"}
            """;

        var pythonNormalized = Normalizer.Apply(pythonBody, DefaultAllowlist);
        var dotnetNormalized = Normalizer.Apply(dotnetBody, DefaultAllowlist);

        Assert.Equal(pythonNormalized.GetRawText(), dotnetNormalized.GetRawText());
    }
}
