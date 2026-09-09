using System.Security.Cryptography;
using System.Text;

namespace ISEStudio.Ontology;

/// <summary>
/// Produces a stable, order-independent signature for a triple set. Two
/// captures of the same logical triples (same subjects, predicates, objects,
/// graphs — regardless of insertion order, and regardless of which overload
/// was used to feed the input) hash to the same signature so the conflict
/// queue can deduplicate re-detected issues.
///
/// <para>Algorithm: route every input — whether a list of
/// <see cref="RdfStatement"/> or a raw N-Quads byte payload — through the
/// same canonical N-Quads writer (<see cref="AppendTerm"/>), sort the
/// resulting lines in lexicographic order, join with <c>\n</c>, then
/// SHA-256, so the two input shapes agree on identical logical
/// content.</para>
///
/// <para>The byte overload parses the payload via
/// <see cref="RdfDotNetRdfCodec.ParseNQuads"/> and re-serializes each parsed
/// statement through the canonical writer. This makes
/// <c>Signature(bytesFromDump)</c> and <c>Signature(statementsFromMatch)</c>
/// return identical hashes for the same layer — verified by
/// <c>Signature_is_consistent_between_byte_and_statement_overloads</c>.</para>
/// </summary>
public static class ConflictDetector
{
    /// <summary>
    /// Compute the canonical SHA-256 signature for a set of statements.
    /// This is the primary runtime entry point; the <c>byte[]</c> overload
    /// parses N-Quads payloads and re-serializes them through the same
    /// canonical writer.
    /// </summary>
    public static string Signature(IReadOnlyList<RdfStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (statements.Count == 0)
        {
            return Sha256Hex(ReadOnlySpan<byte>.Empty);
        }

        var lines = new string[statements.Count];
        for (int i = 0; i < statements.Count; i++)
        {
            lines[i] = CanonicalNQuads(statements[i]);
        }
        Array.Sort(lines, StringComparer.Ordinal);
        var joined = string.Join("\n", lines) + "\n";
        return Sha256Hex(Encoding.UTF8.GetBytes(joined));
    }

    /// <summary>
    /// Compute the signature for raw N-Quads bytes. The bytes are parsed via
    /// <see cref="RdfDotNetRdfCodec.ParseNQuads"/> and each parsed statement
    /// is re-serialized through the same canonical writer the statement
    /// overload uses, so the two overloads agree on identical logical
    /// content. Empty payloads hash to the well-known SHA-256 of the
    /// empty string.
    /// </summary>
    public static string Signature(byte[] nQuads)
    {
        ArgumentNullException.ThrowIfNull(nQuads);
        var text = Encoding.UTF8.GetString(nQuads);
        if (text.Length == 0)
        {
            return Sha256Hex(ReadOnlySpan<byte>.Empty);
        }

        // Round-trip through the codec so the byte path and the statement
        // path produce identical hashes for the same logical content. The
        // parser preserves blank-node labels and graph IRIs (no scoping),
        // so the signature is a semantic fingerprint of the parsed form,
        // not a raw byte fingerprint.
        var parsed = RdfDotNetRdfCodec.ParseNQuads(nQuads);
        return Signature(parsed.Statements);
    }

    private static string CanonicalNQuads(RdfStatement s)
    {
        var sb = new StringBuilder();
        AppendTerm(sb, s.Subject);
        sb.Append(' ');
        AppendTerm(sb, s.PredicateIri);
        sb.Append(' ');
        AppendTerm(sb, s.Object);
        sb.Append(' ');
        if (s.GraphIri is null)
        {
            sb.Append("<>");
        }
        else
        {
            AppendTerm(sb, new RdfIri(s.GraphIri));
        }
        sb.Append(" .");
        return sb.ToString();
    }

    // Thin delegate to the centralised term writer — see NQuadsTermWriter
    // for the canonical N-Quads encoding rules. Conflict signatures must
    // produce the same bytes as the Postgres-layer N-Quads dumps and
    // RdfExportService exports for the same set of statements, so the
    // sites share one implementation rather than copies that can drift.
    private static void AppendTerm(StringBuilder sb, RdfTerm term) =>
        NQuadsTermWriter.Append(sb, term);

    private static void AppendTerm(StringBuilder sb, string predicateIri)
    {
        sb.Append('<').Append(predicateIri).Append('>');
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}