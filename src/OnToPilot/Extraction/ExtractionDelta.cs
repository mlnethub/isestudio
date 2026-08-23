using System.Text.Json;
using OnToPilot.Ontology;

namespace OnToPilot.Extraction;

/// <summary>
/// One chunk's worth of schema candidates returned by the LLM, already
/// normalised into the <see cref="SchemaBuilder"/> mutation vocabulary so the
/// merger can hand it straight to <see cref="SchemaBuilder.BuildMutation"/>.
/// </summary>
public sealed record TBoxDelta(
    IReadOnlyList<ClassMutation> Classes,
    IReadOnlyList<PropertyMutation> ObjectProperties,
    IReadOnlyList<PropertyMutation> DataProperties,
    IReadOnlyList<AxiomMutation> Axioms)
{
    /// <summary>A delta that would write nothing.</summary>
    public static TBoxDelta Empty { get; } = new(
        Array.Empty<ClassMutation>(),
        Array.Empty<PropertyMutation>(),
        Array.Empty<PropertyMutation>(),
        Array.Empty<AxiomMutation>());

    /// <summary>Whether this delta carries no candidates at all.</summary>
    public bool IsEmpty =>
        Classes.Count == 0 && ObjectProperties.Count == 0 &&
        DataProperties.Count == 0 && Axioms.Count == 0;

    /// <summary>Project into the <see cref="OntologyMutation"/> the schema builder consumes.</summary>
    public OntologyMutation ToMutation() =>
        new(Classes, ObjectProperties, DataProperties, Axioms);
}

/// <summary>A single data-property assertion attached to an extracted mention.</summary>
public sealed record AboxAttribute(string Property, string Value);

/// <summary>A single object-property assertion between two extracted mentions.</summary>
public sealed record AboxRelation(string Property, string Target);

/// <summary>
/// One extracted instance mention. <see cref="Class"/> is a TBox class
/// <em>label</em>, not an IRI — resolution against the live class index
/// happens in the merger so unknown labels can be counted instead of
/// silently minting untyped individuals.
/// </summary>
public sealed record AboxIndividual(
    string Label,
    string Class,
    string? Evidence,
    IReadOnlyList<AboxAttribute> Attributes,
    IReadOnlyList<AboxRelation> Relations);

/// <summary>One chunk's worth of instance candidates returned by the LLM.</summary>
public sealed record ABoxDelta(IReadOnlyList<AboxIndividual> Individuals)
{
    /// <summary>A delta that would write nothing.</summary>
    public static ABoxDelta Empty { get; } = new(Array.Empty<AboxIndividual>());

    /// <summary>Whether this delta carries no mentions at all.</summary>
    public bool IsEmpty => Individuals.Count == 0;
}

/// <summary>
/// Tolerant JSON reader for the extraction payloads. LLM replies are
/// frequently wrapped in prose or a fenced code block, and individual fields
/// are routinely missing or the wrong JSON kind, so every accessor degrades to
/// a default rather than throwing: a malformed field costs one candidate, not
/// the whole chunk. A reply with no recoverable JSON object at all yields
/// <see cref="TBoxDelta.Empty"/> / <see cref="ABoxDelta.Empty"/>.
/// </summary>
/// <remarks>
/// Field names mirror <c>backend/app/ontology/extract.py</c> and
/// <c>abox_extract.py</c> exactly (<c>classes</c>, <c>object_properties</c>,
/// <c>data_properties</c>, <c>subclass_of</c>, <c>disjoint_with</c>,
/// <c>equivalent_class</c>; <c>individuals</c> with <c>attributes</c> /
/// <c>relations</c>) so prompts ported from the Python backend keep working
/// unchanged.
/// </remarks>
public static class ExtractionDeltaParser
{
    /// <summary>Parse an LLM reply into a schema delta.</summary>
    public static TBoxDelta ParseTBox(string? reply)
    {
        if (!TryReadObject(reply, out var root)) return TBoxDelta.Empty;

        var classes = new List<ClassMutation>();
        foreach (var item in Items(root, "classes"))
        {
            var label = Str(item, "label");
            if (label.Length == 0) continue;
            classes.Add(new ClassMutation(
                label,
                NullIfEmpty(Str(item, "comment")),
                RoleVerified: false,
                Evidence: NullIfEmpty(Str(item, "evidence"))));
        }

        var objectProperties = ReadProperties(root, "object_properties", "object");
        var dataProperties = ReadProperties(root, "data_properties", "data");

        var axioms = new List<AxiomMutation>();
        foreach (var item in Items(root, "subclass_of"))
        {
            var sub = Str(item, "sub");
            var super = Str(item, "super");
            if (sub.Length == 0 || super.Length == 0) continue;
            axioms.Add(new AxiomMutation(
                "subclass",
                Sub: sub,
                Super: super,
                Evidence: NullIfEmpty(Str(item, "evidence"))));
        }
        foreach (var (field, type) in new[] { ("disjoint_with", "disjoint"), ("equivalent_class", "equivalent") })
        {
            foreach (var item in Items(root, field))
            {
                var a = Str(item, "a");
                var b = Str(item, "b");
                if (a.Length == 0 || b.Length == 0) continue;
                axioms.Add(new AxiomMutation(type, A: a, B: b));
            }
        }

        return new TBoxDelta(classes, objectProperties, dataProperties, axioms);
    }

    /// <summary>Parse an LLM reply into an instance delta.</summary>
    public static ABoxDelta ParseABox(string? reply)
    {
        if (!TryReadObject(reply, out var root)) return ABoxDelta.Empty;

        var individuals = new List<AboxIndividual>();
        foreach (var item in Items(root, "individuals"))
        {
            var label = Str(item, "label");
            var cls = Str(item, "class");
            if (label.Length == 0 || cls.Length == 0) continue;

            var attributes = new List<AboxAttribute>();
            foreach (var attr in Items(item, "attributes"))
            {
                var property = Str(attr, "property");
                var value = Str(attr, "value");
                if (property.Length == 0 || value.Length == 0) continue;
                attributes.Add(new AboxAttribute(property, value));
            }

            var relations = new List<AboxRelation>();
            foreach (var rel in Items(item, "relations"))
            {
                var property = Str(rel, "property");
                var target = Str(rel, "target");
                if (property.Length == 0 || target.Length == 0) continue;
                relations.Add(new AboxRelation(property, target));
            }

            individuals.Add(new AboxIndividual(
                label, cls, NullIfEmpty(Str(item, "evidence")), attributes, relations));
        }

        return new ABoxDelta(individuals);
    }

    private static List<PropertyMutation> ReadProperties(JsonElement root, string field, string kind)
    {
        var properties = new List<PropertyMutation>();
        foreach (var item in Items(root, field))
        {
            var label = Str(item, "label");
            if (label.Length == 0) continue;
            properties.Add(new PropertyMutation(
                label,
                kind,
                NullIfEmpty(Str(item, "comment")),
                NullIfEmpty(Str(item, "domain")),
                NullIfEmpty(Str(item, "range"))));
        }
        return properties;
    }

    // ------------------------------------------------------------------
    // JSON helpers
    // ------------------------------------------------------------------

    private static IEnumerable<JsonElement> Items(JsonElement parent, string field)
    {
        if (parent.ValueKind != JsonValueKind.Object) yield break;
        if (!parent.TryGetProperty(field, out var array)) yield break;
        if (array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
    }

    private static string Str(JsonElement parent, string field)
    {
        if (parent.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!parent.TryGetProperty(field, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>
    /// Pull the first balanced JSON object out of <paramref name="reply"/>.
    /// Handles bare JSON, fenced code blocks, and prose-wrapped JSON. Tracks
    /// brace depth through quoted strings so an unescaped <c>}</c> inside a
    /// string (which models regularly produce) does not cut the object short.
    /// Shared with <see cref="TBoxVerifyService"/> for critic replies.
    /// </summary>
    internal static bool TryReadObject(string? reply, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(reply)) return false;

        var span = reply.AsSpan();
        var start = -1;
        var depth = 0;
        var inString = false;
        var escape = false;
        var objectStart = -1;
        var objectEnd = -1;

        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (escape)
            {
                escape = false;
                continue;
            }
            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }
            if (c == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;

            if (c == '{')
            {
                if (depth == 0) objectStart = i;
                depth++;
                continue;
            }
            if (c == '}')
            {
                depth--;
                if (depth == 0 && objectStart >= 0)
                {
                    objectEnd = i;
                    start = objectStart;
                    break;
                }
            }
        }
        if (start < 0 || objectEnd <= start) return false;

        var candidate = span[start..(objectEnd + 1)].ToString();
        try
        {
            using var document = JsonDocument.Parse(candidate);
            root = document.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
