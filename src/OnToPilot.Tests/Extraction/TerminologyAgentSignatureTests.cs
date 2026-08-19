using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OnToPilot.Extraction;

namespace OnToPilot.Tests.Extraction;

/// <summary>
/// Locks the Python-equivalent signature parity for <see cref="TerminologyAgent"/>.
/// Each test compares <c>TerminologyAgent.ComputeSignature</c> against a
/// SHA-256 value produced independently from the canonical Python expression
/// <c>hashlib.sha256(json.dumps(canonical, ensure_ascii=True, sort_keys=True,
/// separators=(",", ":")).encode("utf-8")).hexdigest()</c>.
///
/// <para>If the encoder, separator strategy, or sort-key behaviour ever drifts
/// from Python's defaults, these tests fail loudly instead of silently
/// breaking cross-stack dedup for non-English terminology.</para>
/// </summary>
public sealed class TerminologyAgentSignatureTests
{
    /// <summary>
    /// ASCII simple create payload. Locks the canonical ordering, compact
    /// separators, and that no byte in the signed range ever changes
    /// unexpectedly. Hash computed with:
    /// <c>sha256('{"action":"create","payload":{"term":"Animal"},"target_iri":null}')</c>.
    /// </summary>
    [Fact]
    public void ComputeSignature_ascii_create_matches_python_hash()
    {
        var payload = new Dictionary<string, object?>
        {
            ["term"] = "Animal",
        };
        var sig = TerminologyAgent.ComputeSignature("create", targetIri: null, payload);

        Assert.Equal(
            "d00e5cfe5e3a0270b53359b44614e4689f90c2e98aca5c1bcd99556e9e48be2d",
            sig);
    }

    /// <summary>
    /// Non-ASCII (Chinese) term. Hash computed with:
    /// <c>sha256('{"action":"create","payload":{"term":"\\u672f\\u8bed"},"target_iri":null}')</c>.
    /// Locks the encoder to <c>JavaScriptEncoder.Default</c> (ensure_ascii
    /// semantics) so a future contributor who switches to an
    /// <c>UnsafeRelaxedJsonEscaping</c> variant (raw UTF-8 bytes) sees this
    /// test fail instead of silently breaking dedup parity for Chinese
    /// terminology.
    /// </summary>
    [Fact]
    public void ComputeSignature_chinese_term_matches_python_hash()
    {
        var payload = new Dictionary<string, object?>
        {
            ["term"] = "术语",
        };
        var sig = TerminologyAgent.ComputeSignature("create", targetIri: null, payload);

        Assert.Equal(
            "ad504c4f010cc486e60cd85140fe5b483c856c118526182a280976a8ad0e650b",
            sig);
    }

    /// <summary>
    /// Non-ASCII (accented Latin-1) value inside a nested array. Hash
    /// computed with:
    /// <c>sha256('{"action":"add_alias","payload":{"add_alt_labels":[{"value":"caf\\u00e9","language":"en"}]},"target_iri":"http://example.org/concept-1"}')</c>.
    /// Locks the same encoder rule plus the nested-object deep escape
    /// (the Python encoder walks every string in the tree).
    /// </summary>
    [Fact]
    public void ComputeSignature_accented_value_matches_python_hash()
    {
        var payload = new Dictionary<string, object?>
        {
            ["add_alt_labels"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["language"] = "en",
                    ["value"] = "café",
                },
            },
        };
        var sig = TerminologyAgent.ComputeSignature(
            "add_alias",
            targetIri: "http://example.org/concept-1",
            payload);

        // Computed independently via:
        //   node sha.js (which simulates
        //   `json.dumps(canonical, ensure_ascii=True, sort_keys=True,
        //   separators=(",", ":")).encode("utf-8")`)
        // → lowercase hex → SHA-256.
        Assert.Equal(
            "123d6cbe1cd686279b363340cec386e47d5c86dd88ba1e969cc1eca1e89330fd",
            sig);
    }

    /// <summary>
    /// Pure black-box check: the signature for a Chinese term must equal
    /// the SHA-256 of the raw UTF-8 bytes of the Python-equivalent JSON
    /// string. If either the encoder or the separator logic drifts, this
    /// assertion points the debugger at <see cref="TerminologyAgent"/>
    /// rather than at any test-side helper.
    /// </summary>
    [Fact]
    public void ComputeSignature_chinese_term_equals_independent_sha256()
    {
        var payload = new Dictionary<string, object?>
        {
            ["term"] = "术语",
        };
        var sig = TerminologyAgent.ComputeSignature("create", targetIri: null, payload);

        // Build the Python-equivalent JSON string by hand (sort_keys +
        // compact separators + \uXXXX escape). This is what the Python
        // backend hashes before SHA-256; if our hash matches, the byte
        // streams match.
        const string expected =
            "{\"action\":\"create\",\"payload\":{\"term\":\"\\u672f\\u8bed\"},\"target_iri\":null}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(expected));
        var expectedHash = Convert.ToHexString(bytes).ToLowerInvariant();

        Assert.Equal(expectedHash, sig);
    }

    /// <summary>
    /// Locks the encoder choice directly: serialising a Chinese label with
    /// the options the agent uses for the signature must produce the
    /// <c>\uXXXX</c> escape form, not raw UTF-8 bytes. This is the unit
    /// test the re-reviewer asked for ("verify the resulting bytes match
    /// what Python would produce for a known non-ASCII test case").
    ///
    /// <para>The raw <see cref="JavaScriptEncoder.Default"/> output is
    /// uppercase (<c>术语</c>); <see cref="TerminologyAgent"/>
    /// post-processes the bytes to lowercase (<c>术语</c>) inside
    /// <c>SerializeCompactBytes</c> to match Python's <c>ensure_ascii</c>.
    /// This test pins the raw encoder behaviour so a future contributor
    /// who swaps the encoder sees the change here.</para>
    /// </summary>
    [Fact]
    public void Encoder_escapes_chinese_to_uXXXX_for_signature_bytes()
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.Default,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes("术语", opts);
        // Each Chinese character must occupy 6 ASCII bytes (\uXXXX), not
        // the 3 UTF-8 bytes it would otherwise be encoded as. The full
        // serialised string is 14 bytes: opening quote, 6 for 术, 6 for
        // 语, closing quote. JavaScriptEncoder.Default emits uppercase
        // hex digits in every position; the lowercase conversion happens
        // inside SerializeCompactBytes, not here.
        var expected = new byte[]
        {
            (byte)'"', (byte)'\\', (byte)'u', (byte)'6', (byte)'7', (byte)'2', (byte)'F',
            (byte)'\\', (byte)'u', (byte)'8', (byte)'B', (byte)'E', (byte)'D',
            (byte)'"',
        };
        Assert.Equal(expected, bytes);
    }
}