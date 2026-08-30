using Microsoft.Extensions.Logging;

namespace ISEStudio.Observability;

/// <summary>
/// Shared observability helpers for LLM-call sites. Centralises the
/// "OperationCanceledException from an LLM call" diagnostic so every chat
/// invocation in the codebase emits the same one-line warning with the
/// same field set.
///
/// <para>The tripwire this guards against: a job row marked
/// "Cancelled (TaskCanceledException)." with no clue whether the SDK hit
/// its internal <c>NetworkTimeout</c> (and the orchestrator should retry
/// with a longer timeout) versus a genuinely-cancelled request (host
/// shutdown, user aborted). Pairing <c>ElapsedSeconds</c> with
/// <c>ConfiguredTimeoutSec</c> answers that question in one log line.</para>
/// </summary>
/// <remarks>
/// Used by <c>TBoxExtractionService.ExtractAsync</c> and
/// <c>TBoxVerifyService.CallAsync</c> today. Future chat-client call sites
/// (terminology agent, conflict agent, structure agent) should route
/// their OCE catch through the same helper so the server log aggregates
/// cleanly across the whole extraction pipeline.
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
}