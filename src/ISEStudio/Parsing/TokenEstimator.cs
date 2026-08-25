using System.Globalization;
using System.Text.RegularExpressions;

namespace ISEStudio.Parsing;

/// <summary>
/// Cheap multilingual token estimator.
///
/// <para>
/// This is a verbatim port of <c>backend/app/parsing/chunker.py::_estimate_tokens</c> and
/// its <c>_TOKEN_PIECES</c> regex. CJK characters generalise to one token per character;
/// Latin / numeric runs average roughly four characters per token (rounded up, with a
/// one-token minimum so very short identifiers still register). The estimate is
/// deliberately conservative — it controls the HybridChunker budget and the number
/// displayed in the UI.
/// </para>
/// </summary>
public static class TokenEstimator
{
    // Three alternative classes: CJK ideographs / kana, ASCII alnum runs, single non-whitespace
    // (catches punctuation + symbols). Must match the Python regex byte-for-byte to keep
    // parity with the frozen manifest.
    private static readonly Regex TokenPieces = new(
        @"[㐀-䶿一-鿿豈-﫿]|[A-Za-z0-9_]+|[^\s]",
        RegexOptions.Compiled);

    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var sum = 0;
        foreach (Match? match in TokenPieces.Matches(text))
        {
            if (match is null || match.Length == 0) continue;
            var piece = match.Value;
            var first = piece[0];
            if (first < 128 && (char.IsLetterOrDigit(first) || first == '_'))
            {
                // Latin/numeric/underscore run: ~4 chars per token, floor at 1.
                sum += Math.Max(1, (int)Math.Ceiling(piece.Length / 4.0));
            }
            else
            {
                // CJK or other single non-whitespace character → 1 token.
                sum += 1;
            }
        }

        // The Python implementation always returns at least 1 so callers can assert the
        // chunk has a non-zero budget.
        return Math.Max(1, sum);
    }
}