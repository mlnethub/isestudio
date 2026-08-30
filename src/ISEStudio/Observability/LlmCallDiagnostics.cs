using Microsoft.Extensions.Logging;

namespace ISEStudio.Observability;

/// <summary>
/// Shared observability helpers for LLM-call sites. Centralises both the
/// <see cref="OperationCanceledException"/> cancellation diagnostic AND
/// the non-OCE failure diagnostic so every chat invocation in the
/// codebase emits the same one-line warning with the same field set,
/// regardless of which way the call ended.
///
/// <para>The OCE tripwire this guards against: a job row marked
/// "Cancelled (TaskCanceledException)." with no clue whether the SDK hit
/// its internal <c>NetworkTimeout</c> (and the orchestrator should retry
/// with a longer timeout) versus a genuinely-cancelled request (host
/// shutdown, user aborted). Pairing <c>ElapsedSeconds</c> with
/// <c>ConfiguredTimeoutSec</c> answers that question in one log line —
/// see <see cref="LogCancellation"/>.</para>
///
/// <para>The non-OCE failure tripwire (added in 2026-08 after the
/// production job where <c>ClientRetryPolicy</c> exhausted retries on a
/// transient 503): without a structured log at the call site, the job
/// row only shows the raw exception type and message — no provider /
/// model / operationName correlation, no elapsed-time hint of how many
/// retries were attempted, no way to filter Datadog for "all
/// <c>Llm.Extract</c> failures in the last hour". See
/// <see cref="LogFailure"/>.</para>
/// </summary>
/// <remarks>
/// Used by every chat-client call site in the extraction pipeline (TBox
/// extract + verify, Hierarchy / Corpus recovery, ABox extract, 4 agents).
/// Future chat-client call sites should route both their OCE catch AND
/// their non-OCE catch through the matching helper so the server log
/// aggregates cleanly across the whole extraction pipeline.
/// </remarks>
public static class LlmCallDiagnostics
{
    /// <summary>
    /// Emit the cancellation diagnostic for one LLM call. The caller is
    /// expected to <c>throw</c> the OCE after invoking this — the helper
    /// only logs; it does not swallow the exception.
    /// </summary>
    /// <param name="logger">Target logger (any category — extract service,
    /// verify service, terminology agent, …).</param>
    /// <param name="operationName">OpenTelemetry activity name (e.g.
    /// <c>"Llm.Extract"</c>, <c>"Llm.TBoxVerify.Denotation"</c>). Echoed
    /// into the warning so the server log makes it clear which call
    /// tripped the cancel.</param>
    /// <param name="provider">Resolved chat provider name (e.g.
    /// <c>"openai-compatible"</c>, <c>"azure-openai"</c>).</param>
    /// <param name="model">Resolved model id (e.g.
    /// <c>"deepseek-v4-flash"</c>).</param>
    /// <param name="elapsedSeconds">Wall-clock seconds from the Stopwatch
    /// started immediately before <c>chat.GetResponseAsync</c>.</param>
    /// <param name="configuredTimeoutSec">
    /// <see cref="ISEStudio.Configuration.ISEStudioOptions.LlmNetworkTimeoutSeconds"/>;
    /// paired with <paramref name="elapsedSeconds"/> to detect SDK timeout
    /// hits. <c>0</c> means "no override; SDK default in effect".
    /// </param>
    /// <param name="isCallerCancelled">Whether the caller-supplied
    /// <see cref="CancellationToken"/> was already cancelled when the OCE
    /// fired. <c>true</c> points at host shutdown / user abort;
    /// <c>false</c> + elapsed ≈ configuredTimeout points at an SDK
    /// internal timeout.</param>
    /// <param name="exception">The caught OCE; surfaced so the full type
    /// name + nested inner type are visible.</param>
    public static void LogCancellation(
        ILogger logger,
        string operationName,
        string provider,
        string model,
        double elapsedSeconds,
        int configuredTimeoutSec,
        bool isCallerCancelled,
        OperationCanceledException exception)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(operationName);
        ArgumentNullException.ThrowIfNull(exception);

        // "<none>" rather than "null" so server-log grep patterns find
        // OCE chains that don't wrap another exception (the common case
        // when the SDK times out — it raises TaskCanceledException with
        // no inner).
        var innerType = exception.InnerException?.GetType().FullName ?? "<none>";

        // Field name `IsCallerCancelled` deliberately avoids the substring
        // "token" — see [[ontopilot-llmcall-redaction-collision]] for the
        // production incident. SecretRedactionProcessor's keyword list
        // includes "token", which would scrub the structured property's
        // value to ***REDACTED*** and make the diagnostic disappear from
        // any sink that filters on the structured side (some Datadog
        // indexes do this). The rendered message body still contains
        // the literal "isCallerCancelled=" so old grep queries that
        // match the body continue to work.
        logger.LogWarning(
            "LLM {OperationName} cancelled after {ElapsedSeconds:F2}s (provider={Provider}, model={Model}, " +
            "configuredTimeoutSec={ConfiguredTimeoutSec}, isCallerCancelled={IsCallerCancelled}, " +
            "exceptionType={ExceptionType}, innerType={InnerType}, message={Message})",
            operationName,
            elapsedSeconds,
            provider,
            model,
            configuredTimeoutSec,
            isCallerCancelled,
            exception.GetType().FullName,
            innerType,
            exception.Message);
    }

    /// <summary>
    /// Emit the failure diagnostic for one LLM call when the chat client
    /// surfaces a non-<see cref="OperationCanceledException"/> error
    /// (HTTP 401 / 403 / 503, retry-exhausted, malformed JSON, etc.). The
    /// caller is expected to <c>throw</c> the exception after invoking
    /// this — the helper only logs; it does not swallow.
    ///
    /// <para>Why a sibling of <see cref="LogCancellation"/>: OCE catches
    /// cover only the cancellation branch. Non-OCE failures (the most
    /// common case is <c>ClientRetryPolicy</c> exhausting retries after a
    /// transient outage) bubble up unlogged today, leaving the job row
    /// with "Extraction failed: HttpRequestException" and no clue which
    /// provider / model / call site tripped, no count of how many retries
    /// were attempted, and no elapsed-time diagnostic to distinguish
    /// "SDK gave up after 5s" from "SDK gave up after 90s of retries".</para>
    ///
    /// <para>Field name hygiene follows the same
    /// <c>SecretRedactionProcessor</c>-safe rule as
    /// <see cref="LogCancellation"/>: <c>OperationName</c>,
    /// <c>ElapsedSeconds</c>, <c>Provider</c>, <c>Model</c>,
    /// <c>ExceptionType</c>, <c>InnerType</c>, <c>Message</c> are all
    /// free of <c>"token"</c> / <c>"prompt"</c> / <c>"secret"</c> /
    /// <c>"bearer"</c> substrings. Note the exception message itself is
    /// passed through verbatim — if it contains one of those substrings
    /// (e.g. an upstream "Authorization: Bearer ..." auth-failure text)
    /// Serilog will redact that property value. Same hygiene constraint as
    /// <see cref="LogCancellation"/>; same workaround if it ever becomes
    /// a real problem (sanitize the message before logging).</para>
    /// </summary>
    /// <param name="logger">Target logger (any category — extract service,
    /// verify service, terminology agent, …).</param>
    /// <param name="operationName">OpenTelemetry activity name (e.g.
    /// <c>"Llm.Extract"</c>, <c>"Llm.TBoxVerify.Critic"</c>).</param>
    /// <param name="provider">Resolved chat provider name (e.g.
    /// <c>"openai-compatible"</c>, <c>"azure-openai"</c>).</param>
    /// <param name="model">Resolved model id (e.g.
    /// <c>"deepseek-v4-flash"</c>).</param>
    /// <param name="elapsedSeconds">Wall-clock seconds from the Stopwatch
    /// started immediately before <c>chat.GetResponseAsync</c>. Useful for
    /// distinguishing "SDK gave up after one attempt" from "SDK gave up
    /// after 5 retries × 18s backoff each".</param>
    /// <param name="exception">The caught non-OCE exception. Type full
    /// name + inner type + message are surfaced as structured fields; the
    /// exception itself becomes the Serilog <c>Exception</c> payload so
    /// the full stack trace still reaches the sink.</param>
    public static void LogFailure(
        ILogger logger,
        string operationName,
        string provider,
        string model,
        double elapsedSeconds,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(operationName);
        ArgumentNullException.ThrowIfNull(exception);

        var innerType = exception.InnerException?.GetType().FullName ?? "<none>";

        logger.LogWarning(
            exception,
            "LLM {OperationName} failed after {ElapsedSeconds:F2}s (provider={Provider}, model={Model}, " +
            "exceptionType={ExceptionType}, innerType={InnerType}, message={Message})",
            operationName,
            elapsedSeconds,
            provider,
            model,
            exception.GetType().FullName,
            innerType,
            exception.Message);
    }
}