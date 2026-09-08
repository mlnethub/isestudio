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
                "set_property_union" or "merge_properties" or "subordinate_properties" or "merge_classes"
                    => await AddGenericAxiomAsync(system, operation, payload, cancellationToken),
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
    private static string PascalCase(string value) => string.Concat(value.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Select(item => char.ToUpperInvariant(item[0]) + item[1..]));
    private static string CamelCase(string value) { var pascal = PascalCase(value); return pascal.Length == 0 ? "property" : char.ToLowerInvariant(pascal[0]) + pascal[1..]; }
    private static string Key(string value) => value.Trim().ToLowerInvariant().Replace(' ', '_');

    private async Task SyncWorkspaceStatementsAsync(
        KnowledgeSystemEntity system,
        string operation,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var all = (await _statements!.ListAsync(system.Id, cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        var tboxGraph = system.GraphIri;
        var aboxGraph = tboxGraph.TrimEnd('/') + "/abox";
        var tbox = all.Where(statement => statement.GraphIri == tboxGraph).ToList();
        var abox = all.Where(statement => statement.GraphIri == aboxGraph).ToList();

        switch (operation)
        {
            case "add_class":
            {
                var iri = ClassIri(system.BaseIri, Required(payload, "label"));
                tbox.Add(new RdfStatement(new RdfIri(iri), Vocabulary.RdfType.Value,
                    new RdfIri(Vocabulary.OwlClass.Value), tboxGraph));
                tbox.Add(new RdfStatement(new RdfIri(iri), Vocabulary.RdfsLabel.Value,
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
        }

        await _statements.ReplaceLayerAsync(system.Id, RdfLayer.TBox.ToString(), tbox, cancellationToken).ConfigureAwait(false);
        await _statements.ReplaceLayerAsync(system.Id, RdfLayer.ABox.ToString(), abox, cancellationToken).ConfigureAwait(false);
    }
}
