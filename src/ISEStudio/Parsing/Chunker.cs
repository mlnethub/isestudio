using System.Text.RegularExpressions;
using System.Text;

namespace ISEStudio.Parsing;

/// <summary>
/// Paragraph-aware greedy chunker with overlap. Verbatim port of
/// <c>backend/app/parsing/chunker.py::chunk_text</c>.
///
/// <para>
/// Algorithm:
/// </para>
/// <list type="number">
///   <item>Split input on blank lines (<c>\n\s*\n</c>), keeping absolute offsets.</item>
///   <item>For each non-empty paragraph: pack into the current buffer; if the buffer
///     would overflow <c>size</c>, flush; oversized paragraphs are sliced at the
///     best nearby sentence/line/paragraph boundary via <see cref="PreferredEnd"/>.</item>
///   <item>Coalesce tiny fragments into a neighbour via <see cref="CoalesceSmallChunks"/>.</item>
///   <item>Apply overlap by extending each chunk's start backwards into the previous
///     chunk's tail, aligning to a structural boundary via <see cref="AlignedOverlapStart"/>.</item>
/// </list>
///
/// <para>The output <see cref="ChunkSpan"/> sequence is byte-identical to the Python
/// implementation for the same input, <c>Size</c>, and <c>Overlap</c>; the
/// <c>ChunkerParityTests</c> lock that contract against the frozen manifest.</para>
/// </summary>
public sealed class Chunker
{
    private static readonly Regex ParaSplit = new(@"\n\s*\n", RegexOptions.Compiled);
    private static readonly Regex SentenceEnd = new(@"[.!?;:。！？；：](?:[""'”’)\]]*)\s+", RegexOptions.Compiled);

    public int Size { get; }
    public int Overlap { get; }

    public Chunker(int size, int overlap)
    {
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 1");
        if (overlap < 0) throw new ArgumentOutOfRangeException(nameof(overlap), "overlap must be >= 0");

        Size = Math.Max(1, size);
        Overlap = Math.Max(0, Math.Min(overlap, Size - 1));
    }

    /// <summary>
    /// Chunk plain text. Returns an empty list for whitespace-only input.
    /// </summary>
    public IReadOnlyList<ChunkSpan> Chunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<ChunkSpan>();
        }

        var indices = new UnicodeIndexMap(text);

        // Replicates Python's split-by-paragraph + offset-tracking loop.
        var paragraphs = new List<(int Start, string Text)>();
        var utf16Pos = 0;
        foreach (var part in ParaSplit.Split(text))
        {
            var utf16Start = text.IndexOf(part, utf16Pos, StringComparison.Ordinal);
            if (utf16Start < 0) utf16Start = utf16Pos;
            paragraphs.Add((indices.ToScalar(utf16Start), part));
            utf16Pos = utf16Start + part.Length;
        }

        var chunks = new List<ChunkSpan>();
        var idx = 0;
        int? bufStart = null;
        var bufEnd = 0;

        void Flush()
        {
            if (bufStart is null) return;
            var span = indices.Slice(bufStart.Value, bufEnd);
            chunks.Add(new ChunkSpan(idx, span, bufStart.Value, bufEnd, TokenEstimator.Estimate(span)));
            idx++;
            bufStart = null;
        }

        foreach (var (start, para) in paragraphs)
        {
            var plen = para.EnumerateRunes().Count();
            if (string.IsNullOrWhiteSpace(para)) continue;

            if (plen > Size)
            {
                // Single oversized paragraph — slice near a structural boundary.
                Flush();
                var segStart = start;
                var paraEnd = start + plen;
                while (segStart < paraEnd)
                {
                    var hardEnd = Math.Min(segStart + Size, paraEnd);
                    var segEnd = PreferredEnd(text, segStart, hardEnd, Size);
                    if (segEnd <= segStart) segEnd = hardEnd;
                    var seg = indices.Slice(segStart, segEnd);
                    chunks.Add(new ChunkSpan(idx, seg, segStart, segEnd, TokenEstimator.Estimate(seg)));
                    idx++;
                    segStart = segEnd;
                }
                continue;
            }

            var candidateEnd = start + plen;
            if (bufStart is not null && candidateEnd - bufStart.Value > Size)
            {
                Flush();
            }
            if (bufStart is null)
            {
                bufStart = start;
            }
            bufEnd = candidateEnd;
        }

        Flush();

        chunks = CoalesceSmallChunks(chunks, text, Size);

        if (Overlap > 0)
        {
            var overlapped = new List<ChunkSpan>(chunks.Count);
            for (var i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                int newStart;
                if (c.Idx > 0)
                {
                    var rawStart = Math.Max(0, c.CharStart - Overlap);
                    newStart = AlignedOverlapStart(text, rawStart);
                }
                else
                {
                    newStart = c.CharStart;
                }
                var seg = indices.Slice(newStart, c.CharEnd);
                overlapped.Add(new ChunkSpan(c.Idx, seg, newStart, c.CharEnd, TokenEstimator.Estimate(seg)));
            }
            chunks = overlapped;
        }

        return chunks;
    }

    /// <summary>
    /// Chunk a <see cref="ParseResult"/>, preferring the structured Docling document when
    /// available and otherwise falling back to plain-text chunking. Mirrors the Python
    /// <c>chunk_document</c> entry point.
    ///
    /// <para>
    /// To opt into the structured path, <see cref="ParseResult.StructuredDocument"/> must
    /// implement <see cref="IDoclingStructuredDocument"/>; callers typically wire this up
    /// when the parser's DoclingDotNet backend produced a real document. Any other
    /// object (or <c>null</c>) falls through silently to <see cref="Chunk(string)"/> on
    /// <see cref="ParseResult.Text"/>. The structured adapter is the integration point for
    /// structure-aware chunking (heading preservation, table-header repetition, etc.) so
    /// downstream code can implement it once and have the rest of the pipeline pick it
    /// up automatically.
    /// </para>
    /// </summary>
    public IReadOnlyList<ChunkSpan> ChunkDocument(ParseResult result)
    {
        if (result.StructuredDocument is { } document)
        {
            var spans = ChunkStructuredDocument(document);
            if (spans is { Count: > 0 })
            {
                return spans;
            }
        }
        return Chunk(result.Text);
    }

    /// <summary>
    /// Chunk a DoclingDotNet structured document. The .NET port cannot replicate
    /// Python's Docling HybridChunker exactly (different version, different OCR/layout
    /// providers), so this fallback emits a simple per-page span based on the
    /// <see cref="IDoclingStructuredDocument"/> adapter that DoclingDotNet consumers
    /// supply. For Task 2 the parity tests only exercise the text path.
    /// </summary>
    private IReadOnlyList<ChunkSpan>? ChunkStructuredDocument(object document)
    {
        if (document is IDoclingStructuredDocument adapter)
        {
            return adapter.ToChunkSpans();
        }
        return null;
    }

    /// <summary>
    /// Move a hard character cut back to the best nearby structural boundary.
    /// Verbatim port of <c>_preferred_end</c>.
    /// </summary>
    internal static int PreferredEnd(string text, int start, int hardEnd, int size)
    {
        var indices = new UnicodeIndexMap(text);
        if (hardEnd >= indices.ScalarCount) return indices.ScalarCount;

        var floor = start + Math.Max(1, (int)(size * 0.6));
        if (floor >= hardEnd) floor = start + 1;

        var window = indices.Slice(floor, hardEnd);
        var paragraph = window.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (paragraph >= 0) return indices.ToScalar(indices.ToUtf16(floor) + paragraph + 2);

        var line = window.LastIndexOf('\n');
        if (line >= 0) return indices.ToScalar(indices.ToUtf16(floor) + line + 1);

        var matches = SentenceEnd.Matches(window);
        if (matches.Count > 0)
        {
            var m = matches[matches.Count - 1];
            return indices.ToScalar(indices.ToUtf16(floor) + m.Index + m.Length);
        }

        var windowRunes = window.EnumerateRunes().ToList();
        for (var i = windowRunes.Count - 1; i >= 0; i--)
        {
            if (Rune.IsWhiteSpace(windowRunes[i])) return floor + i + 1;
        }

        return hardEnd;
    }

    /// <summary>
    /// Align overlap context to a paragraph / sentence / line so it begins with readable
    /// context. Verbatim port of <c>_aligned_overlap_start</c>.
    /// </summary>
    internal static int AlignedOverlapStart(string text, int rawStart)
    {
        if (rawStart <= 0) return 0;

        var indices = new UnicodeIndexMap(text);

        var searchStart = Math.Max(0, rawStart - 400);
        var window = indices.Slice(searchStart, rawStart);

        var paragraph = window.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (paragraph >= 0) return indices.ToScalar(indices.ToUtf16(searchStart) + paragraph + 2);

        var matches = SentenceEnd.Matches(window);
        if (matches.Count > 0)
        {
            var m = matches[matches.Count - 1];
            return indices.ToScalar(indices.ToUtf16(searchStart) + m.Index + m.Length);
        }

        var line = window.LastIndexOf('\n');
        if (line >= 0) return indices.ToScalar(indices.ToUtf16(searchStart) + line + 1);

        var windowRunes = window.EnumerateRunes().ToList();
        for (var i = windowRunes.Count - 1; i >= 0; i--)
        {
            if (Rune.IsWhiteSpace(windowRunes[i])) return searchStart + i + 1;
        }

        // Long unbroken identifiers are rare. Move forward rather than exposing a broken prefix.
        var limit = Math.Min(indices.ScalarCount, rawStart + 240);
        for (var i = rawStart; i < limit; i++)
        {
            if (Rune.IsWhiteSpace(text.EnumerateRunes().ElementAt(i))) return i + 1;
        }
        return rawStart;
    }

    /// <summary>
    /// Fold tiny heading/tail fragments into a neighbour without creating oversized chunks.
    /// Verbatim port of <c>_coalesce_small_chunks</c>.
    /// </summary>
    internal static List<ChunkSpan> CoalesceSmallChunks(IReadOnlyList<ChunkSpan> chunks, string text, int size)
    {
        var minimum = Math.Max(120, (int)(size * 0.35));
        var maximum = Math.Max(size, (int)(size * 1.25));
        var merged = new List<ChunkSpan>();

        foreach (var chunk in chunks)
        {
            if (merged.Count > 0)
            {
                var previous = merged[merged.Count - 1];
                var combinedLength = chunk.CharEnd - previous.CharStart;
                if ((previous.Text.EnumerateRunes().Count() < minimum
                        || chunk.Text.EnumerateRunes().Count() < minimum)
                    && combinedLength <= maximum)
                {
                    var combined = new UnicodeIndexMap(text).Slice(previous.CharStart, chunk.CharEnd);
                    merged[merged.Count - 1] = new ChunkSpan(
                        previous.Idx,
                        combined,
                        previous.CharStart,
                        chunk.CharEnd,
                        TokenEstimator.Estimate(combined));
                    continue;
                }
            }
            merged.Add(chunk);
        }

        // Renumber indices sequentially.
        var result = new List<ChunkSpan>(merged.Count);
        for (var i = 0; i < merged.Count; i++)
        {
            var c = merged[i];
            result.Add(new ChunkSpan(i, c.Text, c.CharStart, c.CharEnd, c.TokenEstimate));
        }
        return result;
    }

    private sealed class UnicodeIndexMap
    {
        private readonly string _text;
        private readonly int[] _scalarToUtf16;

        public UnicodeIndexMap(string text)
        {
            _text = text;
            var offsets = new List<int> { 0 };
            foreach (var rune in text.EnumerateRunes())
            {
                offsets.Add(offsets[^1] + rune.Utf16SequenceLength);
            }
            _scalarToUtf16 = offsets.ToArray();
        }

        public int ScalarCount => _scalarToUtf16.Length - 1;

        public int ToUtf16(int scalarIndex) => _scalarToUtf16[Math.Clamp(scalarIndex, 0, ScalarCount)];

        public int ToScalar(int utf16Index)
        {
            var clamped = Math.Clamp(utf16Index, 0, _text.Length);
            var scalar = Array.BinarySearch(_scalarToUtf16, clamped);
            if (scalar >= 0) return scalar;
            return ~scalar - 1;
        }

        public string Slice(int scalarStart, int scalarEnd)
        {
            var start = ToUtf16(scalarStart);
            var end = ToUtf16(scalarEnd);
            return _text.Substring(start, end - start);
        }
    }
}

/// <summary>
/// Adapter for DoclingDotNet structured documents. The .NET port does not bundle a
/// HybridChunker equivalent (it relies on the Python docling_core hybrid chunker), so
/// callers supply a small adapter that knows how to extract page-aligned spans. Task 2
/// only exercises the text path; this interface is here so the structured-over-text
/// branch compiles cleanly.
/// </summary>
public interface IDoclingStructuredDocument
{
    IReadOnlyList<ChunkSpan> ToChunkSpans();
}