using System.Text.Json;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISEStudio.Ontology;

public sealed class PostgresOntologyRepository : IOntologyRepository
{
    private const string Subclass = "http://www.w3.org/2000/01/rdf-schema#subClassOf";
    private const string Disjoint = "http://www.w3.org/2002/07/owl#disjointWith";
    private const string Equivalent = "http://www.w3.org/2002/07/owl#equivalentClass";

    private readonly ISEStudioDbContext _db;
    private readonly IRdfStatementRepository? _statements;

    public PostgresOntologyRepository(ISEStudioDbContext db, IRdfStatementRepository? statements = null)
    {
        _db = db;
        _statements = statements;
    }

    public async Task<string> ApplyEditAsync(
        string graphIri,
        string baseIri,
        string operation,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken = default)
    {
        var system = await _db.KnowledgeSystems.SingleOrDefaultAsync(
            item => item.GraphIri == graphIri, cancellationToken).ConfigureAwait(false)
            ?? throw new OntologyEditException($"Knowledge system not found for graph: {graphIri}");

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = operation switch
            {
                "add_class" => await AddClassAsync(system, baseIri, payload, cancellationToken),
                "update_class" => await UpdateClassAsync(system, payload, cancellationToken),
                "delete_class" => await DeleteClassAsync(system, payload, cancellationToken),
                "add_property" => await AddPropertyAsync(system, baseIri, payload, cancellationToken),
                "update_property" => await UpdatePropertyAsync(system, baseIri, payload, cancellationToken),
                "delete_property" => await DeletePropertyAsync(system, payload, cancellationToken),
                "add_axiom" => await AddAxiomAsync(system, baseIri, payload, cancellationToken),
                "delete_axiom" => await DeleteAxiomAsync(system, payload, cancellationToken),
                "set_property_union" => await SetPropertyUnionAsync(system, payload, cancellationToken),
                "merge_properties" => await MergePropertiesAsync(system, baseIri, payload, cancellationToken),
                "subordinate_properties" => await SubordinatePropertiesAsync(system, baseIri, payload, cancellationToken),
                "merge_classes" => await AddGenericAxiomAsync(system, operation, payload, cancellationToken),
                _ => throw new OntologyEditException($"Unknown edit op: {operation}")
            };

            system.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (_statements is not null)
            {
                await SyncWorkspaceStatementsAsync(system, operation, payload, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> AddClassAsync(KnowledgeSystemEntity system, string baseIri, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var label = Required(payload, "label");
        var iri = ClassIri(baseIri, label);
        var entity = await _db.EntityTypes.SingleOrDefaultAsync(item => item.KnowledgeSystemId == system.Id && item.Iri == iri, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            _db.EntityTypes.Add(new EntityTypeEntity { KnowledgeSystemId = system.Id, Iri = iri, Key = Key(label), Label = label, Description = Optional(payload, "comment") });
            system.ClassCount++;
        }
        else
        {
            entity.Label ??= label;
            entity.Description = Optional(payload, "comment") ?? entity.Description;
        }
        return iri;
    }

    private async Task<string> UpdateClassAsync(KnowledgeSystemEntity system, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var iri = Required(payload, "iri");
        var entity = await _db.EntityTypes.SingleOrDefaultAsync(item => item.KnowledgeSystemId == system.Id && item.Iri == iri, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return iri;
        }
        if (payload.ContainsKey("label")) entity.Label = Optional(payload, "label");
        if (payload.ContainsKey("comment")) entity.Description = Optional(payload, "comment");
        return iri;
    }

    private async Task<string> DeleteClassAsync(KnowledgeSystemEntity system, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var iri = Required(payload, "iri");
        var entity = await _db.EntityTypes.SingleOrDefaultAsync(item => item.KnowledgeSystemId == system.Id && item.Iri == iri, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return iri;
        }
        var id = entity.Id;
        _db.EntityTypeParents.RemoveRange(_db.EntityTypeParents.Where(item => item.EntityTypeId == id || item.ParentEntityTypeId == id));
        _db.RelationTypeDomains.RemoveRange(_db.RelationTypeDomains.Where(item => item.EntityTypeId == id));
        _db.RelationTypeRanges.RemoveRange(_db.RelationTypeRanges.Where(item => item.EntityTypeId == id));
        _db.OntologyAxioms.RemoveRange(_db.OntologyAxioms.Where(item => item.KnowledgeSystemId == system.Id && (item.SubjectIri == iri || item.ObjectIri == iri)));
        _db.EntityTypes.Remove(entity);
        system.ClassCount = Math.Max(0, system.ClassCount - 1);
        return iri;
    }

    private async Task<string> AddPropertyAsync(KnowledgeSystemEntity system, string baseIri, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var label = Required(payload, "label");
        var iri = PropertyIri(baseIri, label);
        var entity = await _db.RelationTypes.SingleOrDefaultAsync(item => item.KnowledgeSystemId == system.Id && item.Iri == iri, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new RelationTypeEntity { KnowledgeSystemId = system.Id, Iri = iri, Key = Key(label), Label = label, Description = Optional(payload, "comment") };
            _db.RelationTypes.Add(entity);
            system.PropertyCount++;
        }
        await SetPropertyLinksAsync(system, entity, payload, cancellationToken).ConfigureAwait(false);
        return iri;
    }

    private async Task<string> UpdatePropertyAsync(KnowledgeSystemEntity system, string baseIri, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var iri = Required(payload, "iri");
        var entity = await _db.RelationTypes.SingleOrDefaultAsync(item => item.KnowledgeSystemId == system.Id && item.Iri == iri, cancellationToken).ConfigureAwait(false)
            ?? throw new OntologyEditException($"Property not found: {iri}");
        if (payload.ContainsKey("label")) entity.Label = Optional(payload, "label");
        if (payload.ContainsKey("comment")) entity.Description = Optional(payload, "comment");
        await SetPropertyLinksAsync(system, entity, payload, cancellationToken).ConfigureAwait(false);
        return iri;
    }

    private async Task<string> DeletePropertyAsync(KnowledgeSystemEntity system, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var iri = Required(payload, "iri");
        var entity = await _db.RelationTypes.SingleOrDefaultAsync(item => item.KnowledgeSystemId == system.Id && item.Iri == iri, cancellationToken).ConfigureAwait(false)
            ?? throw new OntologyEditException($"Property not found: {iri}");
        _db.RelationTypeDomains.RemoveRange(_db.RelationTypeDomains.Where(item => item.RelationTypeId == entity.Id));
        _db.RelationTypeRanges.RemoveRange(_db.RelationTypeRanges.Where(item => item.RelationTypeId == entity.Id));
        _db.RelationTypes.Remove(entity);
        system.PropertyCount = Math.Max(0, system.PropertyCount - 1);
        return iri;
    }

    private async Task SetPropertyLinksAsync(KnowledgeSystemEntity system, RelationTypeEntity property, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        if (payload.ContainsKey("clear_domain")) _db.RelationTypeDomains.RemoveRange(_db.RelationTypeDomains.Where(item => item.RelationTypeId == property.Id));
        if (payload.ContainsKey("clear_range")) _db.RelationTypeRanges.RemoveRange(_db.RelationTypeRanges.Where(item => item.RelationTypeId == property.Id));
        await AddLinkAsync(system, property, Optional(payload, "domain"), true, cancellationToken).ConfigureAwait(false);
        await AddLinkAsync(system, property, Optional(payload, "range"), false, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddLinkAsync(KnowledgeSystemEntity system, RelationTypeEntity property, string? value, bool domain, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var iri = ClassIri(system.BaseIri, value);
        var entity = await _db.EntityTypes.SingleOrDefaultAsync(item => item.KnowledgeSystemId == system.Id && item.Iri == iri, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new EntityTypeEntity { KnowledgeSystemId = system.Id, Iri = iri, Key = Key(value), Label = value };
            _db.EntityTypes.Add(entity);
            system.ClassCount++;
        }
        if (domain) _db.RelationTypeDomains.Add(new RelationTypeDomainEntity { RelationTypeId = property.Id, EntityTypeId = entity.Id });
        else _db.RelationTypeRanges.Add(new RelationTypeRangeEntity { RelationTypeId = property.Id, EntityTypeId = entity.Id });
    }

    private async Task<string> AddAxiomAsync(KnowledgeSystemEntity system, string baseIri, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var type = Required(payload, "type");
        var (subject, predicate, obj) = type switch
        {
            "subclass" => (ClassIri(baseIri, Required(payload, "sub")), Subclass, ClassIri(baseIri, Required(payload, "super"))),
            "disjoint" => (ClassIri(baseIri, Required(payload, "a")), Disjoint, ClassIri(baseIri, Required(payload, "b"))),
            "equivalent" => (ClassIri(baseIri, Required(payload, "a")), Equivalent, ClassIri(baseIri, Required(payload, "b"))),
            _ => throw new OntologyEditException($"Unknown axiom type: {type}")
        };
        if (subject != obj && !await _db.OntologyAxioms.AnyAsync(item => item.KnowledgeSystemId == system.Id && item.SubjectIri == subject && item.PredicateIri == predicate && item.ObjectIri == obj, cancellationToken).ConfigureAwait(false))
        {
            _db.OntologyAxioms.Add(new OntologyAxiomEntity { KnowledgeSystemId = system.Id, SubjectIri = subject, PredicateIri = predicate, ObjectIri = obj, CreatedAt = DateTimeOffset.UtcNow });
            system.AxiomCount++;
        }
        return type;
    }

    private async Task<string> DeleteAxiomAsync(KnowledgeSystemEntity system, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var type = Required(payload, "type");
        var predicate = type switch { "subclass" => Subclass, "disjoint" => Disjoint, "equivalent" => Equivalent, _ => throw new OntologyEditException($"Unknown axiom type: {type}") };
        var a = Optional(payload, type == "subclass" ? "sub" : "a") ?? throw new OntologyEditException($"{type} requires operands");
        var b = Optional(payload, type == "subclass" ? "super" : "b") ?? throw new OntologyEditException($"{type} requires operands");
        var rows = await _db.OntologyAxioms.Where(item => item.KnowledgeSystemId == system.Id && item.PredicateIri == predicate && ((item.SubjectIri == a && item.ObjectIri == b) || (type != "subclass" && item.SubjectIri == b && item.ObjectIri == a))).ToListAsync(cancellationToken).ConfigureAwait(false);
        _db.OntologyAxioms.RemoveRange(rows);
        system.AxiomCount = Math.Max(0, system.AxiomCount - rows.Count);
        return type;
    }

    private async Task<string> AddGenericAxiomAsync(KnowledgeSystemEntity system, string operation, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var subject = Optional(payload, "iri") ?? Optional(payload, "target") ?? Optional(payload, "source") ?? throw new OntologyEditException($"{operation} requires a resource");
        _db.OntologyAxioms.Add(new OntologyAxiomEntity { KnowledgeSystemId = system.Id, SubjectIri = subject, PredicateIri = $"urn:utopia:edit:{operation}", ObjectValue = Optional(payload, "target"), Payload = JsonDocument.Parse(JsonSerializer.Serialize(payload)), CreatedAt = DateTimeOffset.UtcNow });
        system.AxiomCount++;
        return subject;
    }

    private static string Required(IReadOnlyDictionary<string, object?> payload, string name) => Optional(payload, name) is { Length: > 0 } value ? value : throw new OntologyEditException($"{name} required");
    private static string? Optional(IReadOnlyDictionary<string, object?> payload, string name) => payload.TryGetValue(name, out var value) ? value switch { string text => text.Trim(), JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()?.Trim(), _ => value?.ToString()?.Trim() } : null;
    private static string ClassIri(string baseIri, string value) => value.StartsWith("http://", StringComparison.Ordinal) || value.StartsWith("https://", StringComparison.Ordinal) ? value : baseIri + PascalCase(value);
    private static string PropertyIri(string baseIri, string value) => value.StartsWith("http://", StringComparison.Ordinal) || value.StartsWith("https://", StringComparison.Ordinal) ? value : baseIri + CamelCase(value);
    private static string XsdIri(string value) => Vocabulary.DatatypeNode(value);
    private static string PascalCase(string value) => string.Concat(value.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Select(item => char.ToUpperInvariant(item[0]) + item[1..]));
    private static string CamelCase(string value) { var pascal = PascalCase(value); return pascal.Length == 0 ? "property" : char.ToLowerInvariant(pascal[0]) + pascal[1..]; }
    private static string Key(string value) => value.Trim().ToLowerInvariant().Replace(' ', '_');

    private static List<string> ReadStringArray(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            return new List<string>();
        }
        switch (value)
        {
            case IEnumerable<string> strings:
                return strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            case IEnumerable<object> objects:
                return objects
                    .Select(o => o?.ToString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Cast<string>()
                    .ToList();
            case JsonElement element when element.ValueKind == JsonValueKind.Array:
                return element.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                    .Where(s => s is not null)
                    .Cast<string>()
                    .ToList();
            default:
                return new List<string>();
        }
    }

    /// <summary>
    /// Resolve / create the general target object property (by IRI or
    /// label). <paramref name="forbidden"/> (the source IRIs) is checked
    /// BEFORE any write, so a rejected merge / subordinate (target == a
    /// source) never mutates anything. Mirrors Python <c>_prop_target</c>.
    /// </summary>
    private static string ResolveTargetIri(
        IReadOnlyDictionary<string, object?> payload, string baseIri, HashSet<string> forbidden)
    {
        var target = Optional(payload, "target");
        if (string.IsNullOrEmpty(target))
        {
            var label = Optional(payload, "target_label");
            if (string.IsNullOrEmpty(label))
            {
                throw new OntologyEditException("needs target or target_label");
            }
            target = PropertyIri(baseIri, label);
        }
        if (forbidden.Contains(target))
        {
            throw new OntologyEditException("target cannot be one of the sources");
        }
        return target;
    }

    private async Task<string> MergePropertiesAsync(
        KnowledgeSystemEntity system, string baseIri, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var sources = ReadStringArray(payload, "sources");
        if (sources.Count == 0)
        {
            throw new OntologyEditException("merge_properties needs sources");
        }
        var target = ResolveTargetIri(payload, baseIri, sources.ToHashSet(StringComparer.Ordinal));

        var domains = new HashSet<Guid>();
        var ranges = new HashSet<Guid>();
        var rows = await _db.RelationTypes
            .Where(item => item.KnowledgeSystemId == system.Id && sources.Contains(item.Iri))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var rowDomains = await _db.RelationTypeDomains
                .Where(item => item.RelationTypeId == row.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var rowRanges = await _db.RelationTypeRanges
                .Where(item => item.RelationTypeId == row.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var link in rowDomains) domains.Add(link.EntityTypeId);
            foreach (var link in rowRanges) ranges.Add(link.EntityTypeId);
            _db.RelationTypeDomains.RemoveRange(rowDomains);
            _db.RelationTypeRanges.RemoveRange(rowRanges);
            _db.RelationTypes.Remove(row);
        }
        if (rows.Count > 0)
        {
            system.PropertyCount = Math.Max(0, system.PropertyCount - rows.Count);
        }

        var targetRow = await _db.RelationTypes.SingleOrDefaultAsync(
            item => item.KnowledgeSystemId == system.Id && item.Iri == target, cancellationToken).ConfigureAwait(false);
        var label = Optional(payload, "target_label");
        if (targetRow is null)
        {
            targetRow = new RelationTypeEntity
            {
                KnowledgeSystemId = system.Id,
                Iri = target,
                Key = Key(label ?? target),
                Label = label ?? target,
            };
            _db.RelationTypes.Add(targetRow);
            system.PropertyCount++;
        }
        else if (!string.IsNullOrEmpty(label) && string.IsNullOrEmpty(targetRow.Label))
        {
            targetRow.Label = label;
        }

        // Union the target's own links with the harvested source links.
        foreach (var link in await _db.RelationTypeDomains
            .Where(item => item.RelationTypeId == targetRow.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            domains.Add(link.EntityTypeId);
        }
        foreach (var link in await _db.RelationTypeRanges
            .Where(item => item.RelationTypeId == targetRow.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            ranges.Add(link.EntityTypeId);
        }
        _db.RelationTypeDomains.RemoveRange(_db.RelationTypeDomains.Where(item => item.RelationTypeId == targetRow.Id));
        _db.RelationTypeRanges.RemoveRange(_db.RelationTypeRanges.Where(item => item.RelationTypeId == targetRow.Id));
        foreach (var entityId in domains)
        {
            _db.RelationTypeDomains.Add(new RelationTypeDomainEntity { RelationTypeId = targetRow.Id, EntityTypeId = entityId });
        }
        foreach (var entityId in ranges)
        {
            _db.RelationTypeRanges.Add(new RelationTypeRangeEntity { RelationTypeId = targetRow.Id, EntityTypeId = entityId });
        }
        return target;
    }

    private async Task<string> SubordinatePropertiesAsync(
        KnowledgeSystemEntity system, string baseIri, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var sources = ReadStringArray(payload, "sources");
        if (sources.Count == 0)
        {
            throw new OntologyEditException("subordinate_properties needs sources");
        }
        var target = ResolveTargetIri(payload, baseIri, sources.ToHashSet(StringComparer.Ordinal));

        var label = Optional(payload, "target_label");
        var targetRow = await _db.RelationTypes.SingleOrDefaultAsync(
            item => item.KnowledgeSystemId == system.Id && item.Iri == target, cancellationToken).ConfigureAwait(false);
        if (targetRow is null)
        {
            _db.RelationTypes.Add(new RelationTypeEntity
            {
                KnowledgeSystemId = system.Id,
                Iri = target,
                Key = Key(label ?? target),
                Label = label ?? target,
            });
            system.PropertyCount++;
        }
        else if (!string.IsNullOrEmpty(label) && string.IsNullOrEmpty(targetRow.Label))
        {
            targetRow.Label = label;
        }
        return target;
    }

    private static Task<string> SetPropertyUnionAsync(
        KnowledgeSystemEntity system, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        _ = system;
        _ = cancellationToken;
        // The union expression lives in the graph only (owl:unionOf blank
        // list); the flat EF domain/range links stay untouched. Validate
        // the payload so a bad resolve fails before any write.
        var iri = Required(payload, "iri");
        var members = ReadStringArray(payload, "members");
        if (members.Count < 2)
        {
            throw new OntologyEditException("union needs at least two members");
        }
        return Task.FromResult(iri);
    }

    private async Task SyncWorkspaceStatementsAsync(
        KnowledgeSystemEntity system,
        string operation,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var all = (await _statements!.ListAsync(system.Id, cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        // Normalise the graph IRI once — the raw GraphIri column may have a
        // trailing slash, but every read path (KsContext.TBoxGraph,
        // PostgresRdfGraphStore, ABoxService.LoadClassLabelsAsync, …) goes
        // through GraphIri.TrimEnd('/'). Match that here so the row we
        // write is what the reader looks up.
        var tboxGraph = system.GraphIri.TrimEnd('/');
        var aboxGraph = tboxGraph + "/abox";
        var tbox = all.Where(statement => statement.GraphIri == tboxGraph).ToList();
        var abox = all.Where(statement => statement.GraphIri == aboxGraph).ToList();

        switch (operation)
        {
            case "add_class":
            {
                var iri = ClassIri(system.BaseIri, Required(payload, "label"));
                tbox.Add(new RdfStatement(new RdfIri(iri), Vocabulary.RdfType,
                    new RdfIri(Vocabulary.OwlClass), tboxGraph));
                tbox.Add(new RdfStatement(new RdfIri(iri), Vocabulary.RdfsLabel,
                    new RdfLiteral(Required(payload, "label")), tboxGraph));
                break;
            }
            case "delete_class":
            {
                var iri = Required(payload, "iri");
                tbox.RemoveAll(statement => statement.Subject is RdfIri subject && subject.Value == iri);
                abox.RemoveAll(statement => statement.Object is RdfIri obj && obj.Value == iri);
                break;
            }
            case "add_property":
            {
                var iri = PropertyIri(system.BaseIri, Required(payload, "label"));
                var kind = Optional(payload, "kind") ?? "object";
                var typeIri = kind == "data"
                    ? Vocabulary.OwlDatatypeProperty
                    : Vocabulary.OwlObjectProperty;
                tbox.Add(new RdfStatement(new RdfIri(iri), Vocabulary.RdfType,
                    new RdfIri(typeIri), tboxGraph));
                tbox.Add(new RdfStatement(new RdfIri(iri), Vocabulary.RdfsLabel,
                    new RdfLiteral(Required(payload, "label")), tboxGraph));
                break;
            }
            case "delete_property":
            {
                var iri = Required(payload, "iri");
                tbox.RemoveAll(statement => statement.Subject is RdfIri subject && subject.Value == iri);
                break;
            }
            case "update_property":
            {
                // Mirror the EF row edit on the live TBox graph: rewrite
                // rdfs:domain / rdfs:range so relax_range / fix_violation
                // op dispatch (which only sends {"range": "string"} or
                // {"domain": "..."}) shows up in the read APIs. Clear
                // any prior values for this property first so a range
                // change from "integer" → "string" actually drops the
                // old xsd:integer triple instead of appending.
                var iri = Required(payload, "iri");
                tbox.RemoveAll(statement =>
                    statement.Subject is RdfIri subject
                    && subject.Value == iri
                    && (statement.PredicateIri == Vocabulary.RdfsDomain
                        || statement.PredicateIri == Vocabulary.RdfsRange));
                var domain = Optional(payload, "domain");
                var range = Optional(payload, "range");
                if (!string.IsNullOrWhiteSpace(domain))
                {
                    tbox.Add(new RdfStatement(new RdfIri(iri), Vocabulary.RdfsDomain,
                        new RdfIri(ClassIri(system.BaseIri, domain)), tboxGraph));
                }
                if (!string.IsNullOrWhiteSpace(range))
                {
                    tbox.Add(new RdfStatement(new RdfIri(iri), Vocabulary.RdfsRange,
                        new RdfIri(XsdIri(range)), tboxGraph));
                }
                break;
            }
            case "add_axiom":
            {
                // Mirror the EF axiom row on the live TBox graph: the
                // KnowledgeStatsService refresh (run after every edit)
                // re-counts axioms from rdfs:subClassOf / owl:disjointWith /
                // owl:equivalentClass triples, so the graph must carry the
                // same axiom the row store just persisted or the cached
                // AxiomCount would fall back to 0.
                var type = Required(payload, "type");
                var (subject, predicate, obj) = type switch
                {
                    "subclass" => (ClassIri(system.BaseIri, Required(payload, "sub")), Subclass, ClassIri(system.BaseIri, Required(payload, "super"))),
                    "disjoint" => (ClassIri(system.BaseIri, Required(payload, "a")), Disjoint, ClassIri(system.BaseIri, Required(payload, "b"))),
                    "equivalent" => (ClassIri(system.BaseIri, Required(payload, "a")), Equivalent, ClassIri(system.BaseIri, Required(payload, "b"))),
                    _ => throw new OntologyEditException($"Unknown axiom type: {type}")
                };
                if (subject != obj)
                {
                    tbox.Add(new RdfStatement(new RdfIri(subject), predicate, new RdfIri(obj), tboxGraph));
                }
                break;
            }
            case "delete_axiom":
            {
                var type = Required(payload, "type");
                var predicate = type switch { "subclass" => Subclass, "disjoint" => Disjoint, "equivalent" => Equivalent, _ => throw new OntologyEditException($"Unknown axiom type: {type}") };
                var a = Optional(payload, type == "subclass" ? "sub" : "a") ?? throw new OntologyEditException($"{type} requires operands");
                var b = Optional(payload, type == "subclass" ? "super" : "b") ?? throw new OntologyEditException($"{type} requires operands");
                tbox.RemoveAll(statement =>
                    statement.PredicateIri == predicate
                    && statement.Subject is RdfIri subject
                    && statement.Object is RdfIri obj
                    && ((subject.Value == a && obj.Value == b)
                        || (type != "subclass" && subject.Value == b && obj.Value == a)));
                break;
            }
            case "merge_classes":
            {
                var source = Required(payload, "source");
                var target = Required(payload, "target");
                tbox.RemoveAll(statement => statement.Subject is RdfIri subject && subject.Value == source);
                foreach (var statement in abox.Where(statement => statement.Object is RdfIri obj && obj.Value == source).ToList())
                {
                    abox.Remove(statement);
                    abox.Add(statement with { Object = new RdfIri(target) });
                }
                break;
            }
            case "merge_properties":
            {
                var sources = ReadStringArray(payload, "sources");
                if (sources.Count == 0)
                {
                    break; // EF side already validated (throw happens before the graph sync)
                }
                var srcSet = sources.ToHashSet(StringComparer.Ordinal);
                var target = ResolveTargetIri(payload, system.BaseIri, srcSet);
                var domains = new HashSet<string>(StringComparer.Ordinal);
                var ranges = new HashSet<string>(StringComparer.Ordinal);
                for (var i = tbox.Count - 1; i >= 0; i--)
                {
                    var statement = tbox[i];
                    if (srcSet.Contains(statement.PredicateIri))
                    {
                        // A usage triple (source used as predicate) → repoint.
                        tbox[i] = statement with { PredicateIri = target };
                        continue;
                    }
                    if (statement.Subject is RdfIri subj && srcSet.Contains(subj.Value))
                    {
                        // A source property's own definition triple → drop, harvest d/r.
                        tbox.RemoveAt(i);
                        if (statement.PredicateIri == Vocabulary.RdfsDomain && statement.Object is RdfIri domain)
                        {
                            domains.Add(domain.Value);
                        }
                        else if (statement.PredicateIri == Vocabulary.RdfsRange && statement.Object is RdfIri range)
                        {
                            ranges.Add(range.Value);
                        }
                        continue;
                    }
                    if (statement.Object is RdfIri obj && srcSet.Contains(obj.Value))
                    {
                        // A source referenced as an object — drop.
                        tbox.RemoveAt(i);
                    }
                }
                foreach (var statement in abox.Where(s => srcSet.Contains(s.PredicateIri)).ToList())
                {
                    abox.Remove(statement);
                    abox.Add(statement with { PredicateIri = target });
                }
                EnsureTypedProperty(tbox, tboxGraph, target, Optional(payload, "target_label"));
                UnionSlot(tbox, tboxGraph, target, Vocabulary.RdfsDomain, domains);
                UnionSlot(tbox, tboxGraph, target, Vocabulary.RdfsRange, ranges);
                break;
            }
            case "subordinate_properties":
            {
                var sources = ReadStringArray(payload, "sources");
                if (sources.Count == 0)
                {
                    break;
                }
                var srcSet = sources.ToHashSet(StringComparer.Ordinal);
                var target = ResolveTargetIri(payload, system.BaseIri, srcSet);
                var domains = new HashSet<string>(StringComparer.Ordinal);
                var ranges = new HashSet<string>(StringComparer.Ordinal);
                foreach (var statement in tbox.Where(s => s.Subject is RdfIri subj && srcSet.Contains(subj.Value)))
                {
                    if (statement.PredicateIri == Vocabulary.RdfsDomain && statement.Object is RdfIri domain)
                    {
                        domains.Add(domain.Value);
                    }
                    else if (statement.PredicateIri == Vocabulary.RdfsRange && statement.Object is RdfIri range)
                    {
                        ranges.Add(range.Value);
                    }
                }
                EnsureTypedProperty(tbox, tboxGraph, target, Optional(payload, "target_label"));
                foreach (var src in sources)
                {
                    tbox.Add(new RdfStatement(new RdfIri(src), Vocabulary.RdfsSubPropertyOf, new RdfIri(target), tboxGraph));
                }
                UnionSlot(tbox, tboxGraph, target, Vocabulary.RdfsDomain, domains);
                UnionSlot(tbox, tboxGraph, target, Vocabulary.RdfsRange, ranges);
                break;
            }
            case "set_property_union":
            {
                var iri = Required(payload, "iri");
                var slot = Optional(payload, "slot") ?? "range";
                var predicateIri = slot == "domain" ? Vocabulary.RdfsDomain : Vocabulary.RdfsRange;
                var members = ReadStringArray(payload, "members");
                if (members.Count < 2)
                {
                    throw new OntologyEditException("union needs at least two members");
                }
                GcBlankSubject(tbox, iri, predicateIri);
                var union = "u" + Guid.NewGuid().ToString("N")[..12];
                var cells = members.Select(_ => "c" + Guid.NewGuid().ToString("N")[..12]).ToList();
                tbox.Add(new RdfStatement(new RdfBlankNode(union), Vocabulary.RdfType, new RdfIri(Vocabulary.OwlClass), tboxGraph));
                for (var k = 0; k < cells.Count; k++)
                {
                    tbox.Add(new RdfStatement(new RdfBlankNode(cells[k]), Vocabulary.RdfFirst, new RdfIri(members[k]), tboxGraph));
                    RdfTerm rest = k + 1 < cells.Count ? new RdfBlankNode(cells[k + 1]) : new RdfIri(Vocabulary.RdfNil);
                    tbox.Add(new RdfStatement(new RdfBlankNode(cells[k]), Vocabulary.RdfRest, rest, tboxGraph));
                }
                tbox.Add(new RdfStatement(new RdfBlankNode(union), Vocabulary.OwlUnionOf, new RdfBlankNode(cells[0]), tboxGraph));
                tbox.Add(new RdfStatement(new RdfIri(iri), predicateIri, new RdfBlankNode(union), tboxGraph));
                break;
            }
        }

        await _statements.ReplaceLayerAsync(system.Id, RdfLayer.TBox.ToString(), tbox, cancellationToken).ConfigureAwait(false);
        await _statements.ReplaceLayerAsync(system.Id, RdfLayer.ABox.ToString(), abox, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Make sure <paramref name="targetIri"/> is a typed owl:ObjectProperty
    /// in the pending TBox statement list; attach rdfs:label when the
    /// target was minted from <paramref name="label"/>. Mirrors
    /// <c>OntologyEditor.ResolvePropertyTarget</c>'s write tail.
    /// </summary>
    private static void EnsureTypedProperty(
        List<RdfStatement> tbox, string graphIri, string targetIri, string? label)
    {
        var typed = tbox.Any(s => s.Subject is RdfIri subj && subj.Value == targetIri
            && s.PredicateIri == Vocabulary.RdfType
            && s.Object is RdfIri obj && obj.Value == Vocabulary.OwlObjectProperty);
        if (!typed)
        {
            tbox.Add(new RdfStatement(new RdfIri(targetIri), Vocabulary.RdfType,
                new RdfIri(Vocabulary.OwlObjectProperty), graphIri));
        }
        if (!string.IsNullOrEmpty(label))
        {
            var labelled = tbox.Any(s => s.Subject is RdfIri subj && subj.Value == targetIri
                && s.PredicateIri == Vocabulary.RdfsLabel);
            if (!labelled)
            {
                tbox.Add(new RdfStatement(new RdfIri(targetIri), Vocabulary.RdfsLabel,
                    new RdfLiteral(label), graphIri));
            }
        }
    }

    /// <summary>
    /// Set target's slot to its current values ∪ <paramref name="collected"/>
    /// (single value when the union has one member, an owl:unionOf blank
    /// list otherwise). Mirrors Python <c>_union_slot</c>.
    /// </summary>
    private static void UnionSlot(
        List<RdfStatement> tbox, string graphIri, string targetIri, string predicateIri, HashSet<string> collected)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in tbox)
        {
            if (statement.Subject is RdfIri subj && subj.Value == targetIri
                && statement.PredicateIri == predicateIri && statement.Object is RdfIri obj)
            {
                current.Add(obj.Value);
            }
        }
        var members = new SortedSet<string>(current, StringComparer.Ordinal);
        members.UnionWith(collected);
        if (members.Count == 0)
        {
            return;
        }
        GcBlankSubject(tbox, targetIri, predicateIri);
        if (members.Count == 1)
        {
            tbox.Add(new RdfStatement(new RdfIri(targetIri), predicateIri, new RdfIri(members.First()), graphIri));
            return;
        }
        var memberList = members.ToList();
        var union = "u" + Guid.NewGuid().ToString("N")[..12];
        var cells = memberList.Select(_ => "c" + Guid.NewGuid().ToString("N")[..12]).ToList();
        tbox.Add(new RdfStatement(new RdfBlankNode(union), Vocabulary.RdfType, new RdfIri(Vocabulary.OwlClass), graphIri));
        for (var k = 0; k < cells.Count; k++)
        {
            tbox.Add(new RdfStatement(new RdfBlankNode(cells[k]), Vocabulary.RdfFirst, new RdfIri(memberList[k]), graphIri));
            RdfTerm rest = k + 1 < cells.Count ? new RdfBlankNode(cells[k + 1]) : new RdfIri(Vocabulary.RdfNil);
            tbox.Add(new RdfStatement(new RdfBlankNode(cells[k]), Vocabulary.RdfRest, rest, graphIri));
        }
        tbox.Add(new RdfStatement(new RdfBlankNode(union), Vocabulary.OwlUnionOf, new RdfBlankNode(cells[0]), graphIri));
        tbox.Add(new RdfStatement(new RdfIri(targetIri), predicateIri, new RdfBlankNode(union), graphIri));
    }

    /// <summary>
    /// Remove a subject's slot triples and garbage-collect any blank-node
    /// expression (e.g. a previous owl:unionOf list) reachable from them,
    /// so replacing a union slot leaves no orphans. Mirrors Python
    /// <c>_gc_blank</c>.
    /// </summary>
    private static void GcBlankSubject(List<RdfStatement> tbox, string targetIri, string predicateIri)
    {
        var toRemove = new List<RdfStatement>();
        var stack = new Stack<string>();
        foreach (var statement in tbox.Where(s => s.Subject is RdfIri subj && subj.Value == targetIri
            && s.PredicateIri == predicateIri))
        {
            toRemove.Add(statement);
            if (statement.Object is RdfBlankNode blank)
            {
                stack.Push(blank.Id);
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }
            foreach (var statement in tbox.Where(s => s.Subject is RdfBlankNode blank && blank.Id == current))
            {
                toRemove.Add(statement);
                if (statement.Object is RdfBlankNode obj)
                {
                    stack.Push(obj.Id);
                }
            }
        }
        foreach (var statement in toRemove)
        {
            tbox.Remove(statement);
        }
    }
}
