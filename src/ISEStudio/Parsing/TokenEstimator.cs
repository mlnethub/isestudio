using System.Text;

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
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var sum = 0;
        var runes = text.EnumerateRunes().ToList();
        for (var i = 0; i < runes.Count; i++)
        {
            var rune = runes[i];
            if (rune.IsAscii && (char.IsLetterOrDigit((char)rune.Value) || rune.Value == '_'))
            {
                // Latin/numeric/underscore run: ~4 chars per token, floor at 1.
                var runLength = 1;
                while (i + runLength < runes.Count
                    && runes[i + runLength].IsAscii
                    && (char.IsLetterOrDigit((char)runes[i + runLength].Value)
                        || runes[i + runLength].Value == '_'))
                {
                    runLength++;
                }

                sum += Math.Max(1, (int)Math.Ceiling(runLength / 4.0));
                i += runLength - 1;
            }
            else if (!Rune.IsWhiteSpace(rune))
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