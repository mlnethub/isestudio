using System.Collections.Generic;
using System.Text.Json.Serialization;
using OnToPilot.Application.Foundation;

namespace OnToPilot.Serialization;

/// <summary>
/// System.Text.Json source-generation context for the internal REST surface.
/// Every DTO the controllers emit (success body, error envelope, page
/// containers) is registered here so the serializers are built at compile
/// time and the controllers avoid the reflection-based fallback path.
/// </summary>
/// <remarks>
/// <para>The context intentionally registers only the leaf DTOs each
/// controller returns. Anonymous types used for ad-hoc responses
/// (<c>Ok(new { ... })</c>) still use the runtime serializer &mdash; the
/// internal API contract test only checks the documented schemas so
/// trimming those out keeps the source-generator payload small.</para>
/// </remarks>
[JsonSerializable(typeof(FastApiError))]
[JsonSerializable(typeof(OntologyResponse))]
[JsonSerializable(typeof(IReadOnlyList<OntologyClass>))]
[JsonSerializable(typeof(IReadOnlyList<OntologyProperty>))]
[JsonSerializable(typeof(ChangePreview))]
[JsonSerializable(typeof(QueryResponse))]
internal partial class OnToPilotJsonContext : JsonSerializerContext
{
}