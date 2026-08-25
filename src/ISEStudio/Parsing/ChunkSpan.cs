namespace ISEStudio.Parsing;

/// <summary>
/// A single chunk produced by <see cref="Chunker"/>.
///
/// <para>
/// Mirrors <c>backend/app/parsing/chunker.py::ChunkSpan</c>. <c>CharStart</c> /
/// <c>CharEnd</c> are absolute offsets into the original document text (post-overlap
/// alignment) so downstream code can render the chunk back into the source for
/// citations.
/// </para>
/// </summary>
public sealed record ChunkSpan(
    int Idx,
    string Text,
    int CharStart,
    int CharEnd,
    int TokenEstimate);