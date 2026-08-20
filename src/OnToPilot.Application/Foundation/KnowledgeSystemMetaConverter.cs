using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnToPilot.Application.Foundation;

/// <summary>
/// Polymorphic <see cref="JsonConverter{T}"/> for the ontology view's
/// <c>knowledge_system</c> field. The wire shape is identical in both
/// branches &mdash; <c>{"id": ..., "name": ..., "base_iri": ..., "release"?: ...}</c>
/// &mdash; but the C# representation flips between
/// <see cref="KnowledgeSystemMeta"/> (internal/published systems, primary
/// key is a <see cref="Guid"/>) and <see cref="ExternalKnowledgeSystemMeta"/>
/// (external systems, identifier is the human-readable
/// <c>PublicId</c> string). The discriminator is whether the incoming
/// <c>id</c> parses as a <see cref="Guid"/>; on the wire the frontend
/// sees <c>id: string</c> regardless of the C# branch so the TS type is
/// uniform across both endpoints.
/// </summary>
/// <remarks>
/// <para>The converter lives in <c>OnToPilot.Application.Foundation</c>
/// rather than next to the source-gen context in
/// <c>OnToPilot.Serialization</c> because
/// <see cref="OntologyResponse"/> carries the
/// <c>[property: JsonConverter(typeof(KnowledgeSystemMetaConverter))]</c>
/// binding and the Application project cannot project-reference
/// OnToPilot (the dependency runs the other way). The source-gen
/// context in <c>OnToPilot/Serialization/OnToPilotJsonContext.cs</c>
/// still picks up <see cref="KnowledgeSystemMeta"/> and
/// <see cref="ExternalKnowledgeSystemMeta"/> as leaf types so the
/// converter's runtime payload has matching shape metadata.</para>
/// </remarks>
public sealed class KnowledgeSystemMetaConverter : JsonConverter<object>
{
    /// <inheritdoc />
    public override object? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                $"Expected JSON object for knowledge_system; got {reader.TokenType}.");
        }

        // Materialise the object once so we can inspect each property by
        // name and then build the right record. Reading piecewise would
        // require copying the reader; JsonDocument gives us a stable
        // snapshot without holding the source bytes.
        using var doc = JsonDocument.ParseValue(ref reader);
        JsonElement root = doc.RootElement;

        string? id = null;
        string? name = null;
        string? baseIri = null;
        string? release = null;

        foreach (JsonProperty prop in root.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "id":
                    id = prop.Value.GetString();
                    break;
                case "name":
                    name = prop.Value.GetString();
                    break;
                case "base_iri":
                    baseIri = prop.Value.GetString();
                    break;
                case "release":
                    release = prop.Value.ValueKind == JsonValueKind.Null
                        ? null
                        : prop.Value.GetString();
                    break;
            }
        }

        if (id is null || name is null || baseIri is null)
        {
            throw new JsonException(
                "knowledge_system must include non-null id, name, and base_iri.");
        }

        if (Guid.TryParse(id, out Guid guid))
        {
            return new KnowledgeSystemMeta(guid, name, baseIri, release);
        }

        return new ExternalKnowledgeSystemMeta(id, name, baseIri);
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        object value,
        JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;

            case KnowledgeSystemMeta ksm:
                writer.WriteStartObject();
                writer.WriteString("id", ksm.Id);
                writer.WriteString("name", ksm.Name);
                writer.WriteString("base_iri", ksm.BaseIri);
                if (ksm.Release is null)
                {
                    writer.WriteNull("release");
                }
                else
                {
                    writer.WriteString("release", ksm.Release);
                }
                writer.WriteEndObject();
                return;

            case ExternalKnowledgeSystemMeta eksm:
                writer.WriteStartObject();
                writer.WriteString("id", eksm.PublicId);
                writer.WriteString("name", eksm.Name);
                writer.WriteString("base_iri", eksm.BaseIri);
                writer.WriteEndObject();
                return;

            default:
                throw new JsonException(
                    $"Unexpected runtime type for knowledge_system: {value.GetType().FullName}.");
        }
    }
}