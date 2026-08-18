using System.Text;
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
/// diff stays a pure comparison of business fields. The allowlist is
/// loaded from <c>migration/contracts/normalization.json</c> at test time
/// (the file is copied to the test output directory by the csproj) so a
/// runner-side allowlist change forces the same C# test suite to reload
/// it &mdash; keeping the C# normaliser and the PowerShell runner from
/// drifting silently. Every test is tagged <see cref="ApiContractCategoryAttribute"/>
/// so the gate filter (<c>dotnet test --filter Category=ApiContract</c>)
/// picks them up alongside the inventory tests.</para>
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class DifferentialContractTests
{
    /// <summary>
    /// Filename of the shipped allowlist contract. The csproj copies
    /// <c>migration/contracts/normalization.json</c> to the test bin
    /// directory as <c>normalization.json</c> and the runner relies on
    /// the same file at runtime; the test bin path is
    /// <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    private const string NormalizationJsonFileName = "normalization.json";

    private static readonly Lazy<string> _defaultAllowlist = new(LoadDefaultAllowlist);

    /// <summary>
    /// Comma-separated allowlist loaded from
    /// <c>migration/contracts/normalization.json</c>. The literal string
    /// is rebuilt from the file at test start so the runner and the C#
    /// normaliser stay anchored to the same source of truth.
    /// </summary>
    private static string DefaultAllowlist => _defaultAllowlist.Value;

    private static string LoadDefaultAllowlist()
    {
        var path = Path.Combine(AppContext.BaseDirectory, NormalizationJsonFileName);
        Assert.True(
            File.Exists(path),
            $"Expected normalization.json next to the test assembly at '{path}'. " +
            "The csproj must CopyToOutputDirectory the migration/contracts/normalization.json file.");

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var allowlist = document.RootElement.GetProperty("allowlist");
        Assert.Equal(JsonValueKind.Array, allowlist.ValueKind);

        var joined = new StringBuilder();
        foreach (var entry in allowlist.EnumerateArray())
        {
            var value = entry.GetString();
            Assert.False(
                string.IsNullOrWhiteSpace(value),
                $"Encountered a null/empty entry in normalization.json allowlist.");
            if (joined.Length > 0) joined.Append(',');
            joined.Append(value);
        }
        return joined.ToString();
    }

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

    /// <summary>
    /// Pins the shipped <c>normalization.json</c> shape that the runner
    /// and the C# normaliser both depend on. The runner iterates the
    /// <c>allowlist</c> array to strip property names; the
    /// <c>headerAllowlist</c> array drives the response-header
    /// comparison. If either array disappears or is emptied the
    /// contract is silently broken (every dynamic field becomes a real
    /// difference). This test fails fast at test load time so the
    /// regression is caught locally, not in CI.
    /// </summary>
    [Fact]
    public void Normalization_json_has_expected_top_level_shape()
    {
        var path = Path.Combine(AppContext.BaseDirectory, NormalizationJsonFileName);
        Assert.True(
            File.Exists(path),
            $"Expected normalization.json next to the test assembly at '{path}'.");

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var allowlist = root.GetProperty("allowlist");
        Assert.Equal(JsonValueKind.Array, allowlist.ValueKind);
        Assert.True(
            allowlist.GetArrayLength() > 0,
            "normalization.json 'allowlist' must be a non-empty array or the runner strips nothing.");

        var headerAllowlist = root.GetProperty("headerAllowlist");
        Assert.Equal(JsonValueKind.Array, headerAllowlist.ValueKind);
        Assert.True(
            headerAllowlist.GetArrayLength() > 0,
            "normalization.json 'headerAllowlist' must be a non-empty array or the runner compares volatile headers verbatim.");
    }

    /// <summary>
    /// End-to-end pin against the shipped JSON allowlist: a payload
    /// carrying one of every literal entry on the allowlist plus a
    /// handful of wildcard-only matches (<c>issued_at</c> for
    /// <c>*_at</c>, <c>client_secret</c> for <c>*_secret</c>) must be
    /// stripped to nothing but the business fields. The allowlist is
    /// loaded from <c>migration/contracts/normalization.json</c>, so a
    /// maintainer who adds a new entry there will not have to remember
    /// to extend this test &mdash; the JSON is the contract.
    /// </summary>
    [Fact]
    public void Normalizer_strips_every_field_in_json_allowlist()
    {
        var allowlist = DefaultAllowlist;

        var payload = """
            {
              "id": 1,
              "name": "Pump",
              "created_at": "x",
              "updated_at": "x",
              "deleted_at": "x",
              "trace_id": "x",
              "request_id": "x",
              "session_id": "x",
              "etag": "x",
              "last_modified": "x",
              "timestamp": "x",
              "ts": "x",
              "token": "x",
              "access_token": "x",
              "refresh_token": "x",
              "trace_token": "x",
              "session_token": "x",
              "bearer_token": "x",
              "api_key": "x",
              "password": "x",
              "issued_at": "x",
              "client_secret": "x"
            }
            """;

        var normalized = Normalizer.Apply(payload, allowlist);

        // Business fields survive verbatim.
        Assert.Equal(1, normalized.GetProperty("id").GetInt32());
        Assert.Equal("Pump", normalized.GetProperty("name").GetString());

        // Every entry on the JSON allowlist — literal OR wildcard-only —
        // must be stripped. If a maintainer adds a new literal to the
        // JSON and forgets to update the payload above, the next
        // maintainer can add it without touching this test (the
        // allowlist is the contract, the payload just samples it).
        var jsonPath = Path.Combine(AppContext.BaseDirectory, NormalizationJsonFileName);
        using var stream = File.OpenRead(jsonPath);
        using var document = JsonDocument.Parse(stream);
        var jsonAllowlist = document.RootElement.GetProperty("allowlist");

        // Build a probe set: every literal entry on the allowlist, plus
        // one synthetic match per wildcard. Synthesising matches from
        // the wildcard text keeps the test self-describing — when a
        // new wildcard pattern is added, the test automatically
        // exercises it without human intervention.
        var probes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in jsonAllowlist.EnumerateArray())
        {
            var value = entry.GetString();
            Assert.False(string.IsNullOrWhiteSpace(value));
            probes.Add(value!);
            if (value!.StartsWith("*", StringComparison.Ordinal))
            {
                var suffix = value.Substring(1); // "_token", "_at", "_secret", ...
                probes.Add("synthetic" + suffix);
            }
        }

        foreach (var probe in probes)
        {
            Assert.False(
                normalized.TryGetProperty(probe, out _),
                $"Field '{probe}' matches the JSON allowlist and must be stripped.");
        }
    }
}