using System.Collections.Generic;
using System.Text.Json.Serialization;
using ISEStudio.Application.Foundation;

namespace ISEStudio.Serialization;

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
/// <para>The <see cref="KnowledgeSystemMetaConverter"/> is wired through
/// <c>[property: JsonConverter]</c> on the
/// <see cref="OntologyResponse.KnowledgeSystem"/> positional property,
/// not through this context &mdash; the converter handles the
/// polymorphic write/read itself, so the source generator only needs to
/// know the leaf record shapes (<see cref="KnowledgeSystemMeta"/> and
/// <see cref="ExternalKnowledgeSystemMeta"/>) it may produce.</para>
/// </remarks>
[JsonSerializable(typeof(FastApiError))]
[JsonSerializable(typeof(OntologyResponse))]
[JsonSerializable(typeof(OntologyAxioms))]
[JsonSerializable(typeof(SubclassAxiom))]
[JsonSerializable(typeof(PairAxiom))]
[JsonSerializable(typeof(OntologyStats))]
[JsonSerializable(typeof(KnowledgeSystemMeta))]
[JsonSerializable(typeof(ExternalKnowledgeSystemMeta))]
[JsonSerializable(typeof(IReadOnlyList<OntologyClass>))]
[JsonSerializable(typeof(IReadOnlyList<OntologyProperty>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(ChangePreview))]
[JsonSerializable(typeof(QueryResponse))]
internal partial class ISEStudioJsonContext : JsonSerializerContext
{
}