using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISEStudio.Ontology;

public sealed class PostgresRdfStatementRepository : IRdfStatementRepository
{
    private const string XsdString = "http://www.w3.org/2001/XMLSchema#string";
    private readonly ISEStudioDbContext _db;

    public PostgresRdfStatementRepository(ISEStudioDbContext db)
    {
        _db = db;
    }

    public Task<RdfWriteScope> BeginWriteAsync(Guid knowledgeSystemId, CancellationToken cancellationToken = default)
        => RdfWriteScope.BeginAsync(_db, knowledgeSystemId, cancellationToken);

    public Task<RdfWriteScope> BeginCaptureWriteAsync(Guid knowledgeSystemId, CancellationToken cancellationToken = default)
        => _db.Database.IsNpgsql()
            ? RdfWriteScope.BeginAsync(_db, knowledgeSystemId, cancellationToken)
            : Task.FromResult(RdfWriteScope.Unmanaged());

    public async Task<bool> AppendIfAbsentAsync(Guid knowledgeSystemId, string layer, RdfStatement statement, CancellationToken cancellationToken = default)
    {
        await using var write = await BeginWriteAsync(knowledgeSystemId, cancellationToken).ConfigureAwait(false);
        var row = ToEntity(knowledgeSystemId, layer, statement, DateTimeOffset.UtcNow);
        var exists = await _db.WorkspaceStatements.AsNoTracking().AnyAsync(item =>
            item.KnowledgeSystemId == knowledgeSystemId && item.Layer == layer && item.GraphIri == row.GraphIri
            && item.Subject == row.Subject && item.SubjectKind == row.SubjectKind && item.Predicate == row.Predicate
            && item.Object == row.Object && item.ObjectKind == row.ObjectKind
            && item.Language == row.Language && (item.Datatype == row.Datatype
                || (row.ObjectKind == "literal" && row.Language == null && row.Datatype == null && item.Datatype == XsdString)),
            cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            _db.WorkspaceStatements.Add(row);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return !exists;
    }

    public async Task ReplaceLayerAsync(
        Guid knowledgeSystemId,
        string layer,
        IReadOnlyList<RdfStatement> statements,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layer);
        ArgumentNullException.ThrowIfNull(statements);

        await using var write = await BeginWriteAsync(knowledgeSystemId, cancellationToken).ConfigureAwait(false);

        await _db.WorkspaceStatements
            .Where(item => item.KnowledgeSystemId == knowledgeSystemId && item.Layer == layer)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        // ExecuteDeleteAsync bypasses the change tracker; detach any
        // still-tracked workspace rows so the AddRange below never
        // collides with stale tracked instances holding the same keys.
        // Detach ONLY this entity type — a blanket ChangeTracker.Clear()
        // would also detach the caller's live entities (the conflict row
        // being resolved, the knowledge-system row, …) and silently
        // discard their pending changes.
        foreach (var entry in _db.ChangeTracker.Entries<WorkspaceStatementEntity>().ToList())
        {
            entry.State = EntityState.Detached;
        }

        var now = DateTimeOffset.UtcNow;
        _db.WorkspaceStatements.AddRange(statements.Select(statement => ToEntity(knowledgeSystemId, layer, statement, now)));
        await _db.SaveChangesAsync(cancellationToken);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RdfStatement>> ListAsync(
        Guid knowledgeSystemId,
        string? layer = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.WorkspaceStatements
            .AsNoTracking()
            .Where(item => item.KnowledgeSystemId == knowledgeSystemId);
        if (!string.IsNullOrWhiteSpace(layer)) query = query.Where(item => item.Layer == layer);

        var rows = await query.OrderBy(item => item.Subject).ThenBy(item => item.Predicate)
            .ThenBy(item => item.Object).ToListAsync(cancellationToken);
        return rows.Select(ToStatement).ToList();
    }

    private static WorkspaceStatementEntity ToEntity(Guid knowledgeSystemId, string layer, RdfStatement statement, DateTimeOffset createdAt)
    {
        var subject = Encode(statement.Subject);
        var obj = Encode(statement.Object);
        return new WorkspaceStatementEntity
        {
            KnowledgeSystemId = knowledgeSystemId,
            Layer = layer,
            GraphIri = statement.GraphIri,
            Subject = subject.Value,
            SubjectKind = subject.Kind,
            Predicate = statement.PredicateIri,
            Object = obj.Value,
            ObjectKind = obj.Kind,
            Language = obj.Language,
            Datatype = obj.Datatype,
            CreatedAt = createdAt,
        };
    }

    private static RdfStatement ToStatement(WorkspaceStatementEntity row)
    {
        var subject = Decode(row.Subject, row.SubjectKind, null, null);
        var obj = Decode(row.Object, row.ObjectKind, row.Language, row.Datatype);
        return new RdfStatement(subject, row.Predicate, obj, row.GraphIri);
    }

    private static (string Value, string Kind, string? Language, string? Datatype) Encode(RdfTerm term) => term switch
    {
        RdfIri iri => (iri.Value, "iri", null, null),
        RdfBlankNode blank => (blank.Id, "blank", null, null),
        RdfLiteral literal => (literal.Value, "literal", literal.Language,
            literal.Datatype == XsdString ? null : literal.Datatype),
        _ => throw new ArgumentOutOfRangeException(nameof(term)),
    };

    private static RdfTerm Decode(string value, string kind, string? language, string? datatype) => kind switch
    {
        "iri" => new RdfIri(value),
        "blank" => new RdfBlankNode(value),
        "literal" => new RdfLiteral(value, language, datatype),
        _ => throw new InvalidOperationException($"Unknown RDF term kind '{kind}'."),
    };
}
