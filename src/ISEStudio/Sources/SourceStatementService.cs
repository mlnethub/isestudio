using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ISEStudio.Sources;

/// <summary>One upstream RDF statement; subject_kind defaults to iri, object_kind is required.</summary>
public sealed record SourceStatementRequest(string Id, string Subject, string Predicate, string Object,
    string ObjectKind, string? SubjectKind = null, string? Datatype = null, string? Language = null);

/// <summary>Results in input order; each item is independently atomic.</summary>
public sealed record SourceStatementsResult(IReadOnlyList<SourceStatementResult> Results);

/// <summary>Safe counts and server identifiers, without RDF bodies or credentials.</summary>
public sealed record SourceStatementResult(int Status, Guid? StatementId, string? FactKey, int Added, int Associated);

public sealed class SourceStatementService(IDbContextFactory<ISEStudioDbContext> contexts, SourceService sources, TimeProvider clock)
{
    public async Task<SourceStatementResult> PushAsync(Guid ksId, Guid sourceId, string token, SourceStatementRequest input, CancellationToken ct)
    {
        if (input.ObjectKind == "literal" && input.Object?.Length > 8192)
            return new(413, null, null, 0, 0);
        RdfStatement statement;
        try { statement = Normalize(input); }
        catch (ArgumentException) { return new(400, null, null, 0, 0); }
        statement = ScopeBlankNodes(statement, sourceId);
        var payload = Hash(statement with { GraphIri = null });
        try
        {
            return await PushInTransactionAsync(ksId, sourceId, token, input.Id, statement, payload, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_source_statement_SourceId_ExternalStatementId" })
        {
            return await PushInTransactionAsync(ksId, sourceId, token, input.Id, statement, payload, ct).ConfigureAwait(false);
        }
    }

    private async Task<SourceStatementResult> PushInTransactionAsync(Guid ksId, Guid sourceId, string token,
        string externalId, RdfStatement statement, string payload, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM source WHERE id = {sourceId} FOR UPDATE", ct).ConfigureAwait(false);
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE source SET id = id WHERE id = {sourceId}", ct).ConfigureAwait(false);
        if (!await sources.VerifyAsync(db, ksId, sourceId, token, ct, SourceKind.Statements).ConfigureAwait(false))
            return new(401, null, null, 0, 0);
        await using var write = await RdfWriteScope.BeginAsync(db, ksId, ct).ConfigureAwait(false);
        var prior = await db.SourceStatements.AsNoTracking().SingleOrDefaultAsync(item => item.SourceId == sourceId && item.ExternalStatementId == externalId, ct).ConfigureAwait(false);
        if (prior is not null)
            return prior.PayloadSha256 == payload ? new(200, prior.Id, prior.FactKey, 0, 0) : new(409, prior.Id, prior.FactKey, 0, 0);
        var source = await db.Sources.AsNoTracking().SingleAsync(item => item.Id == sourceId, ct).ConfigureAwait(false);
        var ks = await db.KnowledgeSystems.AsNoTracking().SingleAsync(item => item.Id == ksId, ct).ConfigureAwait(false);
        statement = statement with { GraphIri = ks.GraphIri.TrimEnd('/') + "/abox" };
        var factKey = "rdf|" + Hash(statement);
        var added = await new PostgresRdfStatementRepository(db).AppendIfAbsentAsync(ksId, "ABox", statement, ct).ConfigureAwait(false);
        var record = new SourceStatementEntity { KnowledgeSystemId = ksId, SourceId = sourceId, ExternalStatementId = externalId,
            PayloadSha256 = payload, FactKey = factKey, SourceNameSnapshot = source.Name, CreatedAt = clock.GetUtcNow() };
        db.SourceStatements.Add(record);
        db.SourceStatementFacts.Add(new SourceStatementFactEntity { KnowledgeSystemId = ksId, SourceStatementId = record.Id, FactKey = factKey });
        db.AuditEvents.Add(new AuditEventEntity { KnowledgeSystemId = ksId, Action = "source.statements.push", Summary = "Source statement accepted",
            Detail = JsonDocument.Parse(JsonSerializer.Serialize(new { sourceId, added = added ? 1 : 0, associated = 1 })), CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(200, record.Id, factKey, added ? 1 : 0, 1);
    }

    private static RdfStatement Normalize(SourceStatementRequest input)
    {
        if (string.IsNullOrWhiteSpace(input.Id) || input.Id.Length > 1024) throw new ArgumentException();
        var subject = Term(input.Subject, input.SubjectKind ?? "iri", null, null);
        if (subject is RdfLiteral) throw new ArgumentException();
        return new(subject, Iri(input.Predicate), Term(input.Object, input.ObjectKind, input.Language, input.Datatype), null);
    }

    private static RdfStatement ScopeBlankNodes(RdfStatement statement, Guid sourceId)
    {
        RdfTerm Scope(RdfTerm term)
        {
            if (term is not RdfBlankNode blank) return term;
            var labelHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(blank.Id))).ToLowerInvariant();
            return new RdfBlankNode($"s{sourceId:N}_{labelHash}");
        }

        return statement with { Subject = Scope(statement.Subject), Object = Scope(statement.Object) };
    }

    private static RdfTerm Term(string value, string kind, string? language, string? datatype)
    {
        if (value is null || kind is null) throw new ArgumentException();
        if (kind != "literal" && (language is not null || datatype is not null)) throw new ArgumentException();
        return kind switch {
            "iri" => new RdfIri(Iri(value)),
            "blank" when value.Length is > 0 and <= 2048 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') => new RdfBlankNode(value),
            "literal" when value.Length <= 8192 => Literal(value, language, datatype),
            _ => throw new ArgumentException() };
    }

    private static RdfLiteral Literal(string value, string? language, string? datatype)
    {
        if (language is not null)
        {
            if (datatype is not null || language.Length is < 1 or > 32
                || !System.Text.RegularExpressions.Regex.IsMatch(language, "^[A-Za-z]+(-[A-Za-z0-9]+)*$")) throw new ArgumentException();
            return new(value, language.ToLowerInvariant());
        }
        var normalizedDatatype = datatype is null ? null : Iri(datatype);
        return new(value, Datatype: normalizedDatatype == "http://www.w3.org/2001/XMLSchema#string" ? null : normalizedDatatype);
    }

    private static string Iri(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || "<>\"{}|^`\\".Contains(character))
            || !Uri.TryCreate(value, UriKind.Absolute, out _)) throw new ArgumentException();
        return value;
    }

    private static string Hash(RdfStatement statement)
    {
        static object Encode(RdfTerm term) => term switch {
            RdfIri iri => new object?[] { "iri", iri.Value }, RdfBlankNode blank => new object?[] { "blank", blank.Id },
            RdfLiteral literal => new object?[] { "literal", literal.Value, literal.Language, literal.Datatype }, _ => throw new ArgumentException() };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new object?[] { Encode(statement.Subject), statement.PredicateIri, Encode(statement.Object), statement.GraphIri }))).ToLowerInvariant();
    }
}