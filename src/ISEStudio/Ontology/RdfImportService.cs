using Microsoft.EntityFrameworkCore;
using ISEStudio.Application.Conflicts;
using ISEStudio.Application.Foundation;
using ISEStudio.Audit;
using ISEStudio.Authorization;
using ISEStudio.Conflicts;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Knowledge;
using ISEStudioOptionsConfig = ISEStudio.Configuration.ISEStudioOptions;

namespace ISEStudio.Ontology;

/// <summary>Import mode for <see cref="RdfImportService.ImportAsync(KsContext, RdfLayer, byte[], ImportMode, CancellationToken)"/>.</summary>
public enum ImportMode
{
    /// <summary>Append quads to the layer.</summary>
    Merge,

    /// <summary>Replace the layer contents with the input (clear + merge).</summary>
    Replace,
}

/// <summary>
/// Input bundle for the multipart RDF import endpoint. Mirrors the
/// <c>/api/knowledge/{id}/rdf/import</c> form fields:
/// <c>file</c> (required bytes), <c>filename</c>, <c>target</c>
/// (<c>auto</c> / <c>tbox</c> / <c>abox</c>), <c>strategy</c>
/// (<c>merge</c> / <c>replace</c>), <c>format</c>
/// (<c>auto</c> / <c>turtle</c> / <c>rdfxml</c> / <c>ntriples</c> / <c>jsonld</c>),
/// and the optional <c>base_iri</c>.
/// </summary>
public sealed record RdfImportRequest(
    Guid KnowledgeSystemId,
    byte[] File,
    string Filename,
    string Target,
    string Strategy,
    string Format,
    string? BaseIri);

/// <summary>Aggregate wire-shape returned to <c>/api/knowledge/{id}/rdf/import</c>.</summary>
public sealed record RdfImportResult(
    string Filename,
    string Format,
    string Target,
    string Strategy,
    string? BaseIri,
    int ParsedTriples,
    int TBoxTriples,
    int ABoxTriples,
    int TBoxAdded,
    int TBoxRemoved,
    int ABoxAdded,
    int ABoxRemoved,
    string GraphIri,
    OntologyResponse View,
    IReadOnlyList<ConflictOut> OpenConflicts,
    ABoxValidationReport Validation,
    TerminologyResult? Terminology);

/// <summary>
/// Layered importer for N-Quads payloads. The N-Quads-only overload
/// (<see cref>ImportAsync(KsContext, RdfLayer, byte[], ImportMode, CancellationToken)</see>)
/// is kept for the in-process round-trip tests; the production
/// <c>/api/knowledge/{id}/rdf/import</c> route flows through the
/// multipart-aware <see cref>ImportAsync(RdfImportRequest, Actor, CancellationToken)"/>
/// overload which normalises form fields, parses + partitions the RDF
/// payload, capture/writes each non-empty graph, records byte-exact
/// audit diffs, runs the post-mutation conflict detection, the
/// terminology sync (when automatic and an ABox change landed), the
/// ABox validation, rebuilds the live ontology view, and refreshes the
/// cached KS class/property/axiom counts.
/// </summary>
public sealed class RdfImportService
{
    private readonly IRdfStatementRepository _statements;
    private readonly RdfImportParser _parser;
    private readonly AuditLogService? _audit;
    private readonly ISEStudioDbContext? _db;
    private readonly KnowledgeSystemAccessService? _access;
    private readonly ConflictService? _conflicts;
    private readonly VocabularyService? _vocabulary;
    private readonly ABoxValidator? _validator;
    private readonly OntologyViewBuilder? _viewBuilder;
    private readonly KnowledgeStatsService? _stats;
    private readonly ISEStudioOptionsConfig _options;

    /// <summary>
    /// Store-only constructor for the N-Quads <c>ImportAsync(KsContext, ...)</c>
    /// overload used by the round-trip / term-writer tests. The
    /// multipart-driven <c>ImportAsync(RdfImportRequest, ...)</c> overload
    /// requires the full DI constructor below and throws if the
    /// request-scoped collaborators are missing.
    /// </summary>
    public RdfImportService(IRdfStatementRepository statements, RdfImportParser parser)
    {
        _statements = statements ?? throw new ArgumentNullException(nameof(statements));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _audit = null;
        _db = null;
        _access = null;
        _conflicts = null;
        _vocabulary = null;
        _validator = null;
        _viewBuilder = null;
        _stats = null;
        _options = new ISEStudioOptionsConfig();
    }

    public RdfImportService(
        IRdfStatementRepository statements,
        RdfImportParser parser,
        AuditLogService audit,
        ISEStudioDbContext db,
        KnowledgeSystemAccessService access,
        ConflictService conflicts,
        VocabularyService vocabulary,
        ABoxValidator validator,
        OntologyViewBuilder viewBuilder,
        KnowledgeStatsService stats,
        Microsoft.Extensions.Options.IOptions<ISEStudioOptionsConfig> options)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(viewBuilder);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(options);

        _statements = statements;
        _parser = parser;
        _audit = audit;
        _db = db;
        _access = access;
        _conflicts = conflicts;
        _vocabulary = vocabulary;
        _validator = validator;
        _viewBuilder = viewBuilder;
        _stats = stats;
        _options = options.Value;
    }

    /// <summary>
    /// N-Quads-only importer kept for the round-trip tests
    /// (<c>RdfRoundTripTests</c>, <c>NQuadsTermWriterTests</c>) and any
    /// future in-process caller. The whole layer transition is one atomic
    /// <see cref="IRdfStatementRepository.ReplaceLayerAsync"/> call: the
    /// next state is computed from the incoming statements (merge or
    /// replace) and swapped in as a single write, so a failed parse or
    /// merge never leaves a half-written layer behind.
    /// </summary>
    public async Task ImportAsync(
        KsContext ks,
        RdfLayer layer,
        byte[] nQuads,
        ImportMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentNullException.ThrowIfNull(nQuads);

        var graphIri = LayerGraph(ks, layer);
        var incoming = RdfDotNetRdfCodec.ParseNQuads(nQuads).Statements
            .Select(s => s with { GraphIri = graphIri }).ToList();
        var layerName = layer.ToString();
        var existing = await _statements.ListAsync(ks.KnowledgeSystemId, layerName, cancellationToken).ConfigureAwait(false);
        var next = mode == ImportMode.Replace ? incoming : RdfStatementSet.Merge(existing, incoming);
        await _statements.ReplaceLayerAsync(ks.KnowledgeSystemId, layerName, next, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// End-to-end multipart workflow: normalise form fields, parse +
    /// partition the RDF payload, capture/write each non-empty graph,
    /// record byte-exact audit diffs, run the post-mutation conflict
    /// detection, the terminology sync (when enabled and an ABox
    /// change landed), the ABox validation, rebuild the live ontology
    /// view, and refresh the cached KS class/property/axiom counts.
    /// Returns the wire-shape payload the controller serialises back
    /// to the client.
    /// </summary>
    public async Task<RdfImportResult> ImportAsync(
        RdfImportRequest request,
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.File);
        ArgumentNullException.ThrowIfNull(actor);

        // The multipart workflow needs the full DI collaborator set;
        // the store-only ctor (used by the N-Quads round-trip tests)
        // leaves these null. Fail loudly so a misconfiguration surfaces
        // as a 500 envelope rather than a NullReferenceException.
        if (_db is null || _audit is null || _access is null
            || _conflicts is null || _vocabulary is null
            || _validator is null || _viewBuilder is null || _stats is null)
        {
            throw new InvalidOperationException(
                "RdfImportService was constructed without the full DI " +
                "collaborator set; the multipart workflow is unavailable.");
        }

        if (request.File.Length == 0)
        {
            throw new RdfImportException("The RDF file is empty");
        }
        if (request.File.Length > _options.RdfImportMaxBytes)
        {
            throw new RdfImportException(
                $"RDF file exceeds the {_options.RdfImportMaxBytes}-byte import limit");
        }

        var target = NormaliseTarget(request.Target);
        var strategy = NormaliseStrategy(request.Strategy);
        var format = RdfImportParser.NormalizeFormat(request.Format);

        var ks = await _db.KnowledgeSystems
            .FirstOrDefaultAsync(k => k.Id == request.KnowledgeSystemId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Knowledge system {request.KnowledgeSystemId} not found.");

        var actorUser = await ResolveActorAsync(actor, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "rdf.import requires an authenticated session.");
        if (!await _access.HasAtLeastAsync(
                actorUser, ks, KSRole.Editor, _db, cancellationToken).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException(
                "Editor role required to import RDF into this knowledge system.");
        }

        var ksCtx = KsContext.FromEntity(ks);
        var blankNodeScope = Guid.NewGuid().ToString("N");

        var parsed = _parser.Parse(
            request.File,
            request.Filename,
            format,
            request.BaseIri,
            _options.RdfImportMaxTriples,
            blankNodeScope);

        var partition = _parser.Partition(parsed.Statements, target);

        // Shared GroupId so the two audit rows (TBox + ABox) collapse
        // into one logical "rdf.import" event in the audit trail.
        var groupId = Guid.NewGuid().ToString("N");

        var tboxAdded = 0;
        var tboxRemoved = 0;
        var aboxAdded = 0;
        var aboxRemoved = 0;
        var tboxAddedBytes = Array.Empty<byte>();
        var tboxRemovedBytes = Array.Empty<byte>();
        var aboxAddedBytes = Array.Empty<byte>();
        var aboxRemovedBytes = Array.Empty<byte>();

        if (partition.TBox.Count > 0)
        {
            var (added, removed, addedBytes, removedBytes) = await ImportLayerAsync(
                ks.Id, ksCtx.TBoxGraph, partition.TBox, strategy, cancellationToken).ConfigureAwait(false);
            tboxAdded = added;
            tboxRemoved = removed;
            tboxAddedBytes = addedBytes;
            tboxRemovedBytes = removedBytes;
        }
        if (partition.ABox.Count > 0)
        {
            var (added, removed, addedBytes, removedBytes) = await ImportLayerAsync(
                ks.Id, ksCtx.ABoxGraph, partition.ABox, strategy, cancellationToken).ConfigureAwait(false);
            aboxAdded = added;
            aboxRemoved = removed;
            aboxAddedBytes = addedBytes;
            aboxRemovedBytes = removedBytes;
        }

        // Post-mutation conflict sync (mirrors Python's
        // rdf_import._detect_conflicts after a successful import).
        // semantic=false keeps this fast — the cheap property/value
        // congruence check. The semantic pass is reserved for the
        // explicit conflicts.detect route.
        var openConflicts = await _conflicts
            .SyncAfterOntologyMutationAsync(ks.Id, semantic: false, cancellationToken)
            .ConfigureAwait(false);

        // Terminology sync only when automatic-terms is enabled AND
        // the ABox actually changed (a TBox-only import never produces
        // new terms; the SKOS pass would just walk the unchanged graph).
        TerminologyResult? terminology = null;
        if (_options.AutomaticTerminology && aboxAdded > 0)
        {
            try
            {
                terminology = await _vocabulary
                    .SyncAsync(ks, actor, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Terminology failures must not fail the import — the
                // brief treats the sync as best-effort. VocabularyService
                // already swallows inner errors and surfaces them on the
                // TerminologyResult.Error field; this catch covers the
                // resolve-audit step that runs after the inner pass.
            }
        }

        // One audit row per changed graph, both stamped with the
        // shared GroupId so the timeline collapses them into one event.
        if (partition.TBox.Count > 0)
        {
            await _audit.RecordAsync(
                ks.Id,
                actorUser,
                action: "rdf.import",
                summary: $"Imported {tboxAdded} ontology triples into {ksCtx.TBoxGraph}",
                detail: new Dictionary<string, object?>
                {
                    ["target"] = "tbox",
                    ["strategy"] = strategy,
                    ["format"] = parsed.Format,
                    ["filename"] = request.Filename,
                    ["tbox_added"] = tboxAdded,
                    ["tbox_removed"] = tboxRemoved,
                },
                graph: ksCtx.TBoxGraph,
                added: tboxAddedBytes,
                removed: tboxRemovedBytes,
                groupId: groupId,
                ct: cancellationToken).ConfigureAwait(false);
        }
        if (partition.ABox.Count > 0)
        {
            await _audit.RecordAsync(
                ks.Id,
                actorUser,
                action: "rdf.import",
                summary: $"Imported {aboxAdded} instances into {ksCtx.ABoxGraph}",
                detail: new Dictionary<string, object?>
                {
                    ["target"] = "abox",
                    ["strategy"] = strategy,
                    ["format"] = parsed.Format,
                    ["filename"] = request.Filename,
                    ["abox_added"] = aboxAdded,
                    ["abox_removed"] = aboxRemoved,
                },
                graph: ksCtx.ABoxGraph,
                added: aboxAddedBytes,
                removed: aboxRemovedBytes,
                groupId: groupId,
                ct: cancellationToken).ConfigureAwait(false);
        }

        // Live view rebuild + cached-stats refresh — same end-of-mutation
        // touch-ups every other ontology writer performs.
        var statements = await _statements.ListAsync(ks.Id, "TBox", cancellationToken).ConfigureAwait(false);
        var view = await _viewBuilder
            .BuildFromStatementsAsync(statements, ks.GraphIri, cancellationToken)
            .ConfigureAwait(false);

        var validation = _validator.Validate(ksCtx);

        await _stats.RefreshAsync(ks.Id, cancellationToken).ConfigureAwait(false);

        return new RdfImportResult(
            Filename: request.Filename,
            Format: parsed.Format,
            Target: target,
            Strategy: strategy,
            BaseIri: request.BaseIri,
            ParsedTriples: parsed.Statements.Count,
            TBoxTriples: partition.TBox.Count,
            ABoxTriples: partition.ABox.Count,
            TBoxAdded: tboxAdded,
            TBoxRemoved: tboxRemoved,
            ABoxAdded: aboxAdded,
            ABoxRemoved: aboxRemoved,
            GraphIri: ksCtx.TBoxGraph,
            View: view,
            OpenConflicts: openConflicts,
            Validation: validation,
            Terminology: terminology);
    }

    /// <summary>
    /// Apply the supplied statements scoped to the graph (replacing
    /// first when <paramref name="strategy"/> is <c>replace</c>), and
    /// return the (added, removed) line counts plus the byte-exact
    /// N-Quads diff blobs (the same byte-exact blobs
    /// <see cref="AuditLogService"/> stores). The layer transition is a
    /// single atomic <see cref="IRdfStatementRepository.ReplaceLayerAsync"/>
    /// write, so a failure never leaves a half-applied import.
    /// </summary>
    private async Task<(int Added, int Removed, byte[] AddedBytes, byte[] RemovedBytes)>
        ImportLayerAsync(
            Guid knowledgeSystemId,
            string graphIri,
            IReadOnlyList<RdfStatement> statements,
            string strategy,
            CancellationToken cancellationToken)
    {
        var incoming = statements.Select(statement => statement with { GraphIri = graphIri }).ToList();
        var layer = LayerForGraph(graphIri);
        var existing = await _statements.ListAsync(knowledgeSystemId, layer, cancellationToken).ConfigureAwait(false);
        var next = strategy == "replace" ? incoming : existing.Concat(incoming).Distinct().ToList();
        var addedStatements = next.Except(existing).ToList();
        var removedStatements = existing.Except(next).ToList();
        await _statements.ReplaceLayerAsync(knowledgeSystemId, layer, next, cancellationToken).ConfigureAwait(false);
        var addedBytes = Serialize(addedStatements);
        var removedBytes = Serialize(removedStatements);
        return (CountLines(addedBytes), CountLines(removedBytes), addedBytes, removedBytes);
    }

    private static int CountLines(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static byte[] Serialize(IEnumerable<RdfStatement> statements) =>
        System.Text.Encoding.UTF8.GetBytes(string.Join("\n", statements.Select(statement =>
            $"{Term(statement.Subject)} <{statement.PredicateIri}> {Term(statement.Object)} <{statement.GraphIri}> .")) + "\n");

    private static string Term(RdfTerm term) => term switch
    {
        RdfIri iri => $"<{iri.Value}>",
        RdfBlankNode blank => $"_:{blank.Id}",
        RdfLiteral literal => $"\"{literal.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
        _ => throw new InvalidOperationException("Unsupported RDF term."),
    };

    private static string LayerForGraph(string graphIri) =>
        graphIri.EndsWith("/abox", StringComparison.Ordinal) ? "ABox" : "TBox";

    private async Task<UserEntity?> ResolveActorAsync(
        Actor actor,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(actor.UserId, out var actorId)) return null;
        // _db is null-checked at the top of the multipart ImportAsync
        // overload; the null-forgiving operator documents that invariant.
        return await _db!.Users
            .FirstOrDefaultAsync(u => u.Id == actorId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string NormaliseTarget(string value)
    {
        var v = (value ?? "auto").Trim().ToLowerInvariant();
        return v switch
        {
            "auto" or "tbox" or "abox" => v,
            _ => throw new RdfImportException(
                $"Unsupported target '{value}' (expected auto/tbox/abox)."),
        };
    }

    private static string NormaliseStrategy(string value)
    {
        var v = (value ?? "merge").Trim().ToLowerInvariant();
        return v switch
        {
            "merge" or "replace" => v,
            _ => throw new RdfImportException(
                $"Unsupported strategy '{value}' (expected merge/replace)."),
        };
    }

    private static string LayerGraph(KsContext ks, RdfLayer layer) => ReleaseManager.GraphIriFor(ks, layer);
}