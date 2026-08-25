using Serilog.Core;
using Serilog.Events;

namespace ISEStudio.Observability;

/// <summary>
/// Serilog enricher that scrubs secret-bearing fields from log events before
/// they reach any sink. The brief mandates that logs MUST NOT contain:
///
/// <list type="bullet">
///   <item>Passwords.</item>
///   <item>API keys (including bearer tokens, OAuth tokens, session tokens).</item>
///   <item>Document bodies (parsed text blobs that may carry PII).</item>
/// </list>
///
/// The enricher matches property names case-insensitively against the
/// keyword set in <see cref="SecretKeywords"/>. On a match, the value is
/// replaced with the literal <see cref="RedactedPlaceholder"/>; non-string
/// values are coerced to a string first so the placeholder rule applies
/// uniformly. Property names that don't match are left alone — including
/// legitimate fields like <c>UserName</c> or <c>Email</c>.
///
/// <para>The enricher also recursively walks structured payload values
/// (dictionaries, sequences, .NET POCOs) so a redaction at the top level
/// doesn't miss nested secret properties (e.g. <c>request.headers.api_key</c>
/// inside a <c>HttpRequestSnapshot</c> POCO).</para>
/// </summary>
public sealed class SecretRedactionProcessor : ILogEventEnricher
{
    /// <summary>Placeholder substituted in place of every redacted value.</summary>
    public const string RedactedPlaceholder = "***REDACTED***";

    /// <summary>
    /// Property-name substrings that trigger redaction. Case-insensitive
    /// substring match so <c>api_key</c>, <c>ApiKey</c>, and <c>API-Key-1</c>
    /// all hit. Document body fields land here too: any field whose name
    /// contains <c>document_body</c> or <c>documentbody</c> matches the
    /// <c>body</c> arm of the rule by including both spellings explicitly.
    /// </summary>
    public static readonly IReadOnlyList<string> SecretKeywords = new[]
    {
        "password",
        "passwd",
        "api_key",
        "apikey",
        "api-key",
        "bearer",
        "token",
        "session",
        "secret",
        "prompt",
        "document_body",
        "documentbody",
        "raw_text",
        "rawtext",
        "extracted_text",
    };

    /// <summary>
    /// Field names that match a single substring but represent legitimate
    /// business values and must not be redacted. Any property name
    /// containing one of these substrings is exempt, even if it also
    /// contains a secret keyword. The list is intentionally narrow — when
    /// in doubt, redact.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowList = new[]
    {
        "bearer_count",
        "tokens_per_minute",
        "secret_count",
    };

    /// <summary>
    /// True when <paramref name="key"/> is a property name that should be
    /// redacted. Case-insensitive substring match against
    /// <see cref="SecretKeywords"/>, minus the allowlist. Returns false
    /// for null / empty keys.
    /// </summary>
    public static bool IsSecretKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        foreach (var allow in AllowList)
        {
            if (key.Contains(allow, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        foreach (var keyword in SecretKeywords)
        {
            if (key.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        // Walk every property on the event. We rebuild the property bag
        // rather than mutating it in place because Serilog's LogEvent
        // doesn't expose a public set of properties — the typed
        // AddOrUpdateProperty is the supported mutation path.
        foreach (var existing in logEvent.Properties.ToList())
        {
            if (IsSecretKey(existing.Key))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(
                    existing.Key, new ScalarValue(RedactedPlaceholder)));
                continue;
            }
            if (TryRedactValue(existing.Value, out var redacted))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(
                    existing.Key, redacted));
            }
        }
    }

    private static bool TryRedactValue(LogEventPropertyValue value, out LogEventPropertyValue redacted)
    {
        switch (value)
        {
            case ScalarValue scalar when scalar.Value is null or string:
                // Scalar strings that didn't match a key rule: scanning
                // values is too risky (would false-positive on legitimate
                // text). Leave alone.
                redacted = value;
                return false;
            case DictionaryValue dict:
                {
                    var pairs = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>();
                    var changed = false;
                    foreach (var pair in dict.Elements.ToList())
                    {
                        var key = pair.Key.Value as string;
                        if (key is not null && IsSecretKey(key))
                        {
                            pairs.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                                pair.Key, new ScalarValue(RedactedPlaceholder)));
                            changed = true;
                        }
                        else if (TryRedactValue(pair.Value, out var inner))
                        {
                            pairs.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                                pair.Key, inner));
                            changed = true;
                        }
                        else
                        {
                            pairs.Add(pair);
                        }
                    }
                    if (changed)
                    {
                        redacted = new DictionaryValue(pairs);
                        return true;
                    }
                    redacted = value;
                    return false;
                }
            case SequenceValue seq:
                {
                    var items = new List<LogEventPropertyValue>();
                    var changed = false;
                    foreach (var element in seq.Elements.ToList())
                    {
                        if (TryRedactValue(element, out var inner))
                        {
                            items.Add(inner);
                            changed = true;
                        }
                        else
                        {
                            items.Add(element);
                        }
                    }
                    if (changed)
                    {
                        redacted = new SequenceValue(items);
                        return true;
                    }
                    redacted = value;
                    return false;
                }
            case StructureValue structure:
                {
                    var props = new List<LogEventProperty>();
                    var changed = false;
                    foreach (var prop in structure.Properties.ToList())
                    {
                        if (IsSecretKey(prop.Name))
                        {
                            props.Add(new LogEventProperty(
                                prop.Name, new ScalarValue(RedactedPlaceholder)));
                            changed = true;
                        }
                        else if (TryRedactValue(prop.Value, out var inner))
                        {
                            props.Add(new LogEventProperty(prop.Name, inner));
                            changed = true;
                        }
                        else
                        {
                            props.Add(prop);
                        }
                    }
                    if (changed)
                    {
                        redacted = new StructureValue(props, structure.TypeTag);
                        return true;
                    }
                    redacted = value;
                    return false;
                }
            default:
                redacted = value;
                return false;
        }
    }
}