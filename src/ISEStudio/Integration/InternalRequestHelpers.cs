using System.Text.Json;
using ISEStudio.Application.Foundation;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Integration;

/// <summary>
/// Shared envelope-unpacking + query-parsing helpers used by every
/// <c>IXxxApplicationService</c> implementation under
/// <c>ISEStudio.Integration</c>. Originally these were either private
/// methods on <see cref="InternalOperationDispatcher"/> (file-level
/// shared) or duplicated into <see cref="ABoxApplicationService"/>
/// during the ABox pilot slice; promoting them to a single static class
/// is the 2026-08-28 cross-slice decision recorded in
/// <c>docs/superpowers/specs/2026-08-28-abox-application-service-pilot.md</c> §6.1.
/// <para>
/// All methods are intentionally pure (no DI, no state). The dispatcher
/// arm layer continues to own the service-locator resolution, the
/// <c>RunWithExtractionGuardAsync</c> envelope, and the anonymous
/// snake_case fallback envelopes; this layer only unpacks what each
/// application service already needs.
/// </para>
/// </summary>
public static class InternalRequestHelpers
{
    // ------------------------------------------------------------------
    // JSON deserialization (snake_case + case-insensitive)
    // ------------------------------------------------------------------

    /// <summary>
    /// Shared <see cref="JsonSerializerOptions"/> with
    /// <see cref="JsonNamingPolicy.SnakeCaseLower"/> and case-insensitive
    /// matching, mirroring what <c>Program.cs AddJsonOptions</c>
    /// configures for the controllers — so the wire shape <c>api_key</c> /
    /// <c>base_url</c> maps cleanly onto PascalCase record properties.
    /// </summary>
    public static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Pull <typeparamref name="T"/> out of the loose <c>"_"</c> envelope
    /// key the dispatcher stamps for every internal POST. Direct dict
    /// bodies (no <c>"_"</c> wrapper) are ignored — the Python baseline
    /// always uses the wrapped form, and the contract tests assert on
    /// that shape.
    /// </summary>
    public static T? DeserializeBody<T>(InternalRequest request) where T : class
    {
        if (request.Body is null) return null;
        if (!request.Body.TryGetValue("_", out var raw) || raw is null) return null;
        if (raw is T typed) return typed;
        if (raw is JsonElement element)
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), DeserializeOptions);
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Loose-body IRI extraction
    // ------------------------------------------------------------------

    /// <summary>
    /// Pull the <c>iri</c> field out of a loose-body POST. The
    /// <see cref="Application.Ontology.IndividualRef"/> DTO is the
    /// documented wire shape; we also accept the bare <c>"iri"</c> key so
    /// the body shape stays loose like the Python side. Used by
    /// <c>abox.delete_individual</c>.
    /// </summary>
    public static string? ExtractIriFromBody(InternalRequest request)
    {
        if (request.Body is null) return null;
        if (!request.Body.TryGetValue("_", out var raw) || raw is null)
        {
            return request.Body.TryGetValue("iri", out var iri) ? iri?.ToString() : null;
        }
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.NameEquals("iri") || prop.NameEquals("Iri"))
                {
                    return prop.Value.GetString();
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Variant of <see cref="ExtractIriFromBody"/> that also accepts a
    /// raw <see cref="IReadOnlyDictionary{TKey, TValue}"/> body (the
    /// controller's <c>[FromBody] Dictionary&lt;string, object&gt;</c>
    /// shape). Used by <c>vocabulary.delete_concept</c>.
    /// </summary>
    public static string? ExtractBodyIri(InternalRequest request)
    {
        var body = request.Body;
        if (body is null) return null;

        if (body.TryGetValue("iri", out var raw) && raw is not null)
        {
            return raw.ToString();
        }

        if (body.TryGetValue("_", out var wrapped) && wrapped is not null)
        {
            if (wrapped is IReadOnlyDictionary<string, object?> dict
                && dict.TryGetValue("iri", out var inner)
                && inner is not null)
            {
                return inner.ToString();
            }
            if (wrapped is JsonElement wrappedEl
                && wrappedEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in wrappedEl.EnumerateObject())
                {
                    if (prop.NameEquals("iri") || prop.NameEquals("Iri"))
                    {
                        return prop.Value.GetString();
                    }
                }
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Query string parsing
    // ------------------------------------------------------------------

    /// <summary>Look up a single query string key, returning <c>null</c> when absent.</summary>
    public static string? QueryString(InternalRequest request, string key) =>
        request.Query is not null && request.Query.TryGetValue(key, out var v) ? v : null;

    /// <summary>Look up a single query string key as an <see cref="int"/>, falling back to a default.</summary>
    public static int QueryInt(InternalRequest request, string key, int fallback) =>
        request.Query is not null && request.Query.TryGetValue(key, out var v)
            && int.TryParse(v, out var n)
            ? n
            : fallback;

    // ------------------------------------------------------------------
    // Loose-dictionary body shape
    // ------------------------------------------------------------------

    /// <summary>
    /// Deserialize the request body as a loose <see cref="Dictionary{TKey, TValue}"/>
    /// (no declared type) for endpoints where the JSON comes in
    /// pre-shaped from the frontend / MCP (e.g.
    /// <c>vocabulary.accept_proposal</c> <c>{note, payload}</c>). The
    /// snake_case / case-insensitive policy still applies through the
    /// underlying JSON element conversion.
    /// </summary>
    public static Dictionary<string, object?>? DeserializeLooseBody(InternalRequest request)
    {
        if (request.Body is null) return null;
        if (!request.Body.TryGetValue("_", out var raw) || raw is null) return null;
        if (raw is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in element.EnumerateObject())
            {
                dict[prop.Name] = JsonElementToObject(prop.Value);
            }
            return dict;
        }
        if (raw is Dictionary<string, object?> alreadyDict)
        {
            return alreadyDict;
        }
        return null;
    }

    /// <summary>
    /// Pull the optional <c>payload</c> override for an accept proposal
    /// decision. Supports both a pre-deserialized dictionary and a
    /// <see cref="JsonElement"/> (the shape <see cref="DeserializeBody{T}"/>
    /// materialises for nested <c>object</c> values).
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? ExtractPayload(
        Dictionary<string, object?>? body)
    {
        if (body is null) return null;
        if (body.TryGetValue("payload", out var p) && p is JsonElement el
            && el.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in el.EnumerateObject())
            {
                dict[prop.Name] = JsonElementToObject(prop.Value);
            }
            return dict;
        }
        if (body.TryGetValue("payload", out var p2) && p2 is IReadOnlyDictionary<string, object?> rd)
        {
            return rd;
        }
        return null;
    }

    /// <summary>
    /// Pull the optional <c>chunk_ids</c> list out of a loose body (the
    /// <c>vocabulary.suggest_terms</c> shape).
    /// </summary>
    public static IReadOnlyList<Guid> ExtractChunkIds(Dictionary<string, object?>? body)
    {
        if (body is null) return Array.Empty<Guid>();
        if (!body.TryGetValue("chunk_ids", out var raw) || raw is null) return Array.Empty<Guid>();
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            var ids = new List<Guid>(el.GetArrayLength());
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && Guid.TryParse(item.GetString(), out var g))
                {
                    ids.Add(g);
                }
            }
            return ids;
        }
        return Array.Empty<Guid>();
    }

    /// <summary>
    /// Coerce a <see cref="JsonElement"/> into the closest CLR type, used
    /// by <see cref="DeserializeLooseBody"/> + <see cref="ExtractPayload"/>
    /// to avoid <c>object?</c> holding raw <see cref="JsonElement"/>
    /// handles after deserialization.
    /// </summary>
    public static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : (object)el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };

    // ------------------------------------------------------------------
    // KS resolution
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolve the <see cref="KnowledgeSystemEntity"/> backing
    /// <paramref name="request"/>'s <c>KnowledgeSystemGuid</c>. Returns
    /// <c>null</c> when the KS id is missing or doesn't match a row. Used
    /// by every workspace-side slice (vocabulary / releases / ontology /
    /// extraction / resolution) that needs the full entity to build a
    /// <c>KsContext</c>.
    /// </summary>
    public static async Task<KnowledgeSystemEntity?> ResolveKsAsync(
        Guid? knowledgeSystemId,
        IServiceProvider services,
        CancellationToken ct)
    {
        if (knowledgeSystemId is null) return null;
        var db = services.GetService(typeof(ISEStudioDbContext)) as ISEStudioDbContext;
        if (db is null) return null;
        return await db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == knowledgeSystemId.Value, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve the <see cref="KnowledgeSystemEntity"/> by its public_id
    /// (NOT internal Guid — external callers never see the internal id).
    /// Used by every external / published slice.
    /// </summary>
    public static async Task<KnowledgeSystemEntity?> ResolveKsByPublicIdAsync(
        string? publicId,
        IServiceProvider services,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(publicId)) return null;
        var db = services.GetService(typeof(ISEStudioDbContext)) as ISEStudioDbContext;
        if (db is null) return null;
        return await db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.PublicId == publicId, ct)
            .ConfigureAwait(false);
    }
}
