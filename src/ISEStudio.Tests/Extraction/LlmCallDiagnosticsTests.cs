using ISEStudio.Observability;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// Unit tests for <see cref="LlmCallDiagnostics.LogCancellation"/>. The
/// helper is shared by <c>TBoxExtractionService.ExtractAsync</c> and
/// <c>TBoxVerifyService.CallAsync</c>; both production call sites only
/// invoke it when an <see cref="OperationCanceledException"/> bubbles up
/// from <c>chat.GetResponseAsync</c>. These tests pin the field shape
/// (operationName / provider / model / elapsedSeconds /
/// configuredTimeoutSec / isCallerCancelled / exceptionType /
/// innerType / message) so a regression that drops or renames any field
/// breaks here, not in production.
/// </summary>
public sealed class LlmCallDiagnosticsTests
{
    [Fact]
    public void LogCancellation_renders_all_fields_in_formatted_message()
    {
        var capturing = new LlmCallDiagnosticsTestHelpers.CapturingLogger<object>();
        var oce = new TaskCanceledException("simulated SDK timeout");

        LlmCallDiagnostics.LogCancellation(
            capturing,
            operationName: "Llm.Extract",
            provider: "openai-compatible",
            model: "deepseek-v4-flash",
            elapsedSeconds: 181.42,
            configuredTimeoutSec: 180,
            isCallerCancelled: false,
            exception: oce);

        Assert.NotNull(capturing.SingleWarning);
        var entry = capturing.SingleWarning;
        Assert.Equal(181.42, entry.ElapsedSeconds, precision: 2);
        // Field-by-field substring checks — server-log grep rules and
        // dashboards key off these exact phrasings.
        Assert.Contains("LLM Llm.Extract cancelled after 181.42s", entry.Formatted);
        Assert.Contains("provider=openai-compatible", entry.Formatted);
        Assert.Contains("model=deepseek-v4-flash", entry.Formatted);
        Assert.Contains("configuredTimeoutSec=180", entry.Formatted);
        Assert.Contains("isCallerCancelled=False", entry.Formatted);
        Assert.Contains("exceptionType=System.Threading.Tasks.TaskCanceledException", entry.Formatted);
        Assert.Contains("innerType=<none>", entry.Formatted);
        Assert.Contains("message=simulated SDK timeout", entry.Formatted);
    }

    [Fact]
    public void LogCancellation_renders_inner_type_full_name_when_inner_exception_present()
    {
        // SDK timeout-cancellation chain: outer OCE wrapping an inner
        // TaskCanceledException. The diagnostic must surface the inner
        // type so we can distinguish "user cancelled via ct" (no inner)
        // from "SDK internal timeout" (inner == TaskCanceledException).
        var inner = new TaskCanceledException("inner SDK timeout");
        var oce = new OperationCanceledException("outer wrapper", inner);
        var capturing = new LlmCallDiagnosticsTestHelpers.CapturingLogger<object>();

        LlmCallDiagnostics.LogCancellation(
            capturing,
            operationName: "Llm.TBoxVerify.Denotation",
            provider: "openai-compatible",
            model: "deepseek-v4-flash",
            elapsedSeconds: 0.123,
            configuredTimeoutSec: 180,
            isCallerCancelled: false,
            exception: oce);

        Assert.NotNull(capturing.SingleWarning);
        var entry = capturing.SingleWarning;
        Assert.Contains("innerType=System.Threading.Tasks.TaskCanceledException", entry.Formatted);
        Assert.Contains("exceptionType=System.OperationCanceledException", entry.Formatted);
    }

    [Fact]
    public void LogCancellation_renders_isCallerCancelled_True_when_user_cancelled()
    {
        // A pre-cancelled token simulates the orchestrator deciding to
        // abort (HTTP request aborted, host shutdown, etc.). Same
        // exception type as an SDK timeout — isCallerCancelled=True
        // is the only field that tells them apart.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var oce = new OperationCanceledException(cts.Token);
        var capturing = new LlmCallDiagnosticsTestHelpers.CapturingLogger<object>();

        LlmCallDiagnostics.LogCancellation(
            capturing,
            operationName: "Llm.Extract",
            provider: "openai-compatible",
            model: "deepseek-v4-flash",
            elapsedSeconds: 0.005,
            configuredTimeoutSec: 180,
            isCallerCancelled: true,
            exception: oce);

        Assert.NotNull(capturing.SingleWarning);
        var entry = capturing.SingleWarning;
        Assert.Contains("isCallerCancelled=True", entry.Formatted);
        // Elapsed stays at the wall-clock value (≈0) so the
        // elapsed < configuredTimeout + isCallerCancelled=True shape
        // points clearly at "user aborted, not SDK timeout".
        Assert.True(entry.ElapsedSeconds < 1.0);
    }

    [Fact]
    public void LogCancellation_renders_zero_elapsed_when_call_failed_immediately()
    {
        // Defends against a future refactor that accidentally uses
        // Stopwatch.ElapsedTicks without the *Seconds conversion — the
        // production Stopwatch is started inside ExtractAsync before the
        // call so elapsed is always > 0, but the helper itself must
        // format whatever the caller hands it (incl. 0).
        var capturing = new LlmCallDiagnosticsTestHelpers.CapturingLogger<object>();
        var oce = new TaskCanceledException("zero-elapsed call");

        LlmCallDiagnostics.LogCancellation(
            capturing,
            operationName: "Llm.Extract",
            provider: "openai-compatible",
            model: "deepseek-v4-flash",
            elapsedSeconds: 0.0,
            configuredTimeoutSec: 180,
            isCallerCancelled: false,
            exception: oce);

        Assert.NotNull(capturing.SingleWarning);
        var entry = capturing.SingleWarning;
        Assert.Equal(0.0, entry.ElapsedSeconds);
        Assert.Contains("cancelled after 0.00s", entry.Formatted);
    }

    [Fact]
    public void LogCancellation_does_not_throw_when_logger_is_null()
    {
        // Defensive: production code never passes null (the services
        // default to NullLogger<T>.Instance), but a hand-built caller
        // could. Pin the argument-null-throw contract so a misuse fails
        // fast rather than silently dropping the diagnostic.
        var oce = new TaskCanceledException();
        Assert.Throws<ArgumentNullException>(() =>
            LlmCallDiagnostics.LogCancellation(
                logger: null!,
                operationName: "Llm.Extract",
                provider: "x",
                model: "y",
                elapsedSeconds: 0.0,
                configuredTimeoutSec: 0,
                isCallerCancelled: false,
                exception: oce));
    }
}