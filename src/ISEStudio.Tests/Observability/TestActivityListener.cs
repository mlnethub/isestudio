using System.Collections.Concurrent;
using System.Diagnostics;

namespace ISEStudio.Tests.Observability;

/// <summary>
/// Test-side subscription for one or more <see cref="ActivitySource"/>s.
/// Records every <see cref="Activity"/> started while the listener is
/// active; the captured list is the input the assertions in
/// <c>TelemetryTests</c> diff against.
///
/// <para>The listener captures <c>ActivityStopped</c> events (rather than
/// just <c>ActivityStarted</c>) so a finished activity carries its
/// final tag set — including the tags stamped by
/// <see cref="ISEStudio.Observability.TelemetryExtensions.WithLlmActivity"/>
/// in its <c>finally</c> block.</para>
/// </summary>
public sealed class TestActivityListener : IDisposable
{
    private readonly ActivityListener _listener;

    /// <summary>Every activity captured by the listener, in completion order.</summary>
    public ConcurrentQueue<Activity> Activities { get; } = new();

    /// <summary>
    /// Subscribe to every supplied source name until the listener is
    /// disposed. Pass the constant names from
    /// <see cref="ISEStudio.Observability.Telemetry"/> to capture only the
    /// ISEStudio-owned sources.
    /// </summary>
    public static TestActivityListener Capture(params string[] sourceNames)
    {
        var listener = new TestActivityListener(sourceNames);
        return listener;
    }

    private TestActivityListener(string[] sourceNames)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => sourceNames.Contains(source.Name, StringComparer.Ordinal),
            // Sample everything — the test asserts on tag presence, not
            // sampling, and a missing sample would mask a regression.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Activities.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Snapshot of <see cref="Activities"/> as a list (FIFO).</summary>
    public IReadOnlyList<Activity> Snapshot() => Activities.ToArray();

    public void Dispose() => _listener.Dispose();
}