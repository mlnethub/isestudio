using System.Text.Json;
using OnToPilot.Parsing;

namespace OnToPilot.Tests.Parsing;

/// <summary>
/// Parity tests against <c>migration/fixtures/parsing/manifest.json</c>, the frozen
/// output of <c>backend/scripts/export_parsing_fixtures.py</c>. The manifest captures
/// what the Python <c>app.parsing.chunker.chunk_text</c> produces for three canonical
/// inputs (English / Chinese / mixed) at <c>size=24</c>, <c>overlap=6</c>. This
/// fixture is the load-bearing contract for the .NET port.
/// </summary>
public sealed class ChunkerParityTests
{
    private const string ManifestPath = "Fixtures/parsing/manifest.json";

    private static IReadOnlyDictionary<string, object> LoadManifest()
    {
        using var stream = File.OpenRead(ManifestPath);
        using var doc = JsonDocument.Parse(stream);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(
            doc.RootElement.GetRawText())!;
    }

    private static Chunker CreateChunker(int size, int overlap) =>
        new(size, overlap);

    private static int GetInt(JsonElement el, string name) => el.GetProperty(name).GetInt32();
    private static string GetString(JsonElement el, string name) => el.GetProperty(name).GetString()!;

    public static IEnumerable<object[]> CaseNames()
    {
        yield return new object[] { "english" };
        yield return new object[] { "chinese" };
        yield return new object[] { "mixed" };
    }

    [Theory]
    [Trait("Category", "Parsing")]
    [MemberData(nameof(CaseNames))]
    public void Chunker_matches_fixture(string caseName)
    {
        using var stream = File.OpenRead(ManifestPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var size = root.GetProperty("size").GetInt32();
        var overlap = root.GetProperty("overlap").GetInt32();
        var caseEl = root.GetProperty("cases").GetProperty(caseName);

        // Inputs are encoded into the .NET side via the manifest itself so the test stays
        // self-contained. We re-derive them from the expected spans' text concatenation order
        // is unreliable, so instead we keep a small table of inputs keyed by case name.
        var text = caseName switch
        {
            "english" => "First sentence. Second sentence.\n\nThird paragraph.",
            "chinese" => "第一句。第二句。\n\n第三段。",
            "mixed" => "Pump P-101 温度为 80°C。Next sentence.",
            _ => throw new InvalidOperationException($"Unknown case: {caseName}"),
        };

        var chunker = CreateChunker(size, overlap);
        var actual = chunker.Chunk(text);

        var expected = caseEl.EnumerateArray().ToList();
        Assert.Equal(expected.Count, actual.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            var exp = expected[i];
            var act = actual[i];
            Assert.Equal(GetInt(exp, "idx"), act.Idx);
            Assert.Equal(GetString(exp, "text"), act.Text);
            Assert.Equal(GetInt(exp, "char_start"), act.CharStart);
            Assert.Equal(GetInt(exp, "char_end"), act.CharEnd);
            Assert.Equal(GetInt(exp, "token_estimate"), act.TokenEstimate);
        }
    }

    [Fact]
    [Trait("Category", "Parsing")]
    public void Chunker_empty_text_returns_empty_list()
    {
        var chunker = CreateChunker(24, 6);
        Assert.Empty(chunker.Chunk(""));
        Assert.Empty(chunker.Chunk("   \n\n  \n  "));
    }

    [Fact]
    [Trait("Category", "Parsing")]
    public void Chunker_overlap_aligns_to_boundary()
    {
        // Build a deterministic multi-paragraph input; overlap should start at a structural
        // boundary inside the previous chunk (paragraph / sentence / line) rather than
        // mid-word.
        var text = string.Join(
            "\n\n",
            "Alpha sentence one. Alpha sentence two.",
            "Bravo sentence one. Bravo sentence two.",
            "Charlie sentence one. Charlie sentence two.");

        var chunker = CreateChunker(48, 12);
        var chunks = chunker.Chunk(text);
        Assert.True(chunks.Count >= 2, "Need at least 2 chunks for overlap test.");

        for (var i = 1; i < chunks.Count; i++)
        {
            var span = chunks[i];
            Assert.True(span.CharStart < span.CharEnd, "Chunk must have positive length.");
            Assert.True(span.CharStart >= chunks[i - 1].CharStart,
                "Overlap must not start before the previous chunk begins.");

            // The overlap start must not be in the middle of a non-whitespace token.
            // Walk back from char_start; if we find a non-space char before a space,
            // the alignment failed.
            if (span.CharStart == 0) continue;
            var prev = text[span.CharStart - 1];
            Assert.True(
                prev == ' ' || prev == '\n' || prev == '\t' || prev == '\r',
                $"Overlap at chunk {i} starts mid-token at offset {span.CharStart} (preceding char: '{prev}').");
        }
    }
}