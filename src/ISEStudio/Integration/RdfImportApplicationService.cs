using ISEStudio.Application.Conflicts;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Extraction;
using ISEStudio.Ontology;

namespace ISEStudio.Integration;

/// <summary>
/// Application service for the single <c>rdf.import</c> dispatcher arm
/// (13/13 slice). Unpacks the multipart
/// <see cref="InternalRequest.Body"/> (<c>file</c> bytes + form
/// fields), builds the <see cref="RdfImportRequest"/>, delegates to
/// <see cref="RdfImportService"/>, and projects the aggregate
/// <see cref="RdfImportResult"/> to the snake_case wire envelope
/// (including the nested conflict / validation / terminology shapes).
/// Returns <c>null</c> when the body is missing so the dispatcher
/// degrades to its empty import envelope.
/// </summary>
public sealed class RdfImportApplicationService : IRdfImportApplicationService
{
    private readonly RdfImportService _import;

    public RdfImportApplicationService(RdfImportService import)
    {
        _import = import;
    }

    public async Task<object?> ImportAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            throw new InvalidOperationException(
                "Knowledge system id is required for rdf.import.");
        }

        if (request.Body is null)
        {
            return null;
        }

        var body = request.Body;
        var file = body.TryGetValue("file", out var rawFile) ? rawFile as byte[] : null;
        if (file is null)
        {
            throw new RdfImportException("file is required and must be non-empty");
        }

        var req = new RdfImportRequest(
            KnowledgeSystemId: request.KnowledgeSystemGuid.Value,
            File: file,
            Filename: body.TryGetValue("filename", out var fn) && fn is string fns ? fns : "upload.ttl",
            Target: body.TryGetValue("target", out var tg) && tg is string tgs ? tgs : "auto",
            Strategy: body.TryGetValue("strategy", out var st) && st is string sts ? sts : "merge",
            Format: body.TryGetValue("format", out var ft) && ft is string fts ? fts : "auto",
            BaseIri: body.TryGetValue("base_iri", out var bi) && bi is string bis ? bis : null);

        var result = await _import.ImportAsync(req, request.Actor, ct)
            .ConfigureAwait(false);
        return ProjectRdfImportResult(result);
    }

    private static object ProjectRdfImportResult(RdfImportResult result) => new
    {
        filename = result.Filename,
        format = result.Format,
        target = result.Target,
        strategy = result.Strategy,
        base_iri = result.BaseIri,
        parsed_triples = result.ParsedTriples,
        tbox_triples = result.TBoxTriples,
        abox_triples = result.ABoxTriples,
        tbox_added = result.TBoxAdded,
        tbox_removed = result.TBoxRemoved,
        abox_added = result.ABoxAdded,
        abox_removed = result.ABoxRemoved,
        graph_iri = result.GraphIri,
        view = result.View,
        open_conflicts = result.OpenConflicts.Select(ProjectConflictOut).ToArray(),
        validation = new
        {
            error_count = result.Validation.ErrorCount,
            warning_count = result.Validation.WarningCount,
            truncated = result.Validation.Truncated,
            violations = result.Validation.Violations.Select(v => new
            {
                id = v.Id,
                type = v.Type,
                severity = v.Severity,
                individual = v.Individual,
                summary = v.Summary,
                fixes = v.Fixes,
            }).ToArray(),
        },
        terminology = result.Terminology is null ? null : new
        {
            scheme_iri = result.Terminology.SchemeIri,
            terms_added = result.Terminology.TermsAdded,
            terms_mapped = result.Terminology.TermsMapped,
            proposals_queued = result.Terminology.ProposalsQueued,
            properties = result.Terminology.Properties,
            aliases_added = result.Terminology.AliasesAdded,
            broader_added = result.Terminology.BroaderAdded,
            stale_mappings_removed = result.Terminology.StaleMappingsRemoved,
            mapping_conflicts = result.Terminology.MappingConflicts,
            error = result.Terminology.Error,
        },
    };

    private static object ProjectConflictOut(ConflictOut c) => new
    {
        id = c.Id,
        knowledge_system_id = c.KnowledgeSystemId,
        signature = c.Signature,
        ctype = c.Ctype,
        severity = c.Severity,
        status = c.Status,
        title = c.Title,
        detail = c.Detail,
        created_at = c.CreatedAt,
        resolved_at = c.ResolvedAt,
        resolution = c.Resolution,
    };
}
