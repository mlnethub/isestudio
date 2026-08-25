using System.Text.Json.Serialization;

namespace ISEStudio.Serialization;

/// <summary>
/// FastAPI's canonical error envelope: a single <c>detail</c> field whose
/// value is a string for plain messages, an object for structured validation
/// problems, or an array for batch failures. The Python backend emits this
/// shape for every 4xx/5xx response and existing client tooling depends on it
/// &mdash; mirroring it in .NET keeps the migration drop-in.
/// </summary>
/// <remarks>
/// <para>Wrapped as a <c>record</c> with a single <see cref="object"/>
/// payload so the same type works for <c>"detail": "Not authenticated"</c>,
/// <c>"detail": { "field": "..." }</c>, and <c>"detail": [...]</c> cases.
/// The actual JSON property name is forced to <c>detail</c> via the
/// <see cref="JsonPropertyNameAttribute"/>.</para>
/// </remarks>
public sealed record FastApiError(
    [property: JsonPropertyName("detail")] object Detail);