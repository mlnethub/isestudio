using ISEStudio.Application.Foundation;
using ISEStudio.Application.Ontology;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-side contract for the workspace <c>ontology.*</c>
/// dispatcher arms. Each method takes an <see cref="InternalRequest"/>
/// envelope (path / query / body / actor) and returns either the
/// strongly-typed DTO the dispatcher serialises, or <c>null</c> when
/// the knowledge system can't be resolved / the role gate trips.
///
/// <para>The dispatcher arm layer retains the schema-compatible empty
/// payload fallback envelopes (<c>EmptyOntologyResponse()</c> /
/// <c>EmptyKnowledgeSystem()</c> / <c>Array.Empty&lt;object&gt;()</c>
/// / <c>string.Empty</c>) &mdash; the application service returns
/// <c>null</c> and the dispatcher substitutes the right shape.
/// See <c>docs/superpowers/specs/2026-08-28-vocabulary-application-service.md</c>
/// §3.3 for the wrapper pattern.</para>
///
/// <para>Cross-surface reads (<c>published.ontology</c> /
/// <c>published.release.ontology</c>) share <see cref="GetPublishedAsync"/>
/// — the dispatcher forwards both arms to it, threading either
/// <c>null</c> (current deployment) or the pinned release
/// <c>version</c> string through <c>request.ResourceId</c>.</para>
/// </summary>
public interface IOntologyApplicationService
{
    /// <summary>Curated TBox view for one knowledge system.</summary>
    Task<OntologyResponse?> GetAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>Apply a single structured edit against the TBox graph.</summary>
    Task<OntologyEditResult?> EditAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>Serialize the TBox graph in one of <c>EXPORT_FORMATS</c>.</summary>
    /// <remarks>Returns the UTF-8 string form of the bytes; the controller wraps
    /// it in a <c>Content(...)</c> result with the matching media type.</remarks>
    Task<string?> ExportAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>Drop every quad in the KS TBox + ABox graphs.</summary>
    Task<OntologyEditResult?> ResetAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>Per-axiom provenance aggregation.</summary>
    Task<IReadOnlyList<ProvenanceGroupOut>?> ProvenanceAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>Per-source document roll-up.</summary>
    Task<IReadOnlyList<SourceOut>?> SourcesAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Published-release TBox view. Shared by <c>published.ontology</c>
    /// (current deployment, <paramref name="request.ResourceId"/> is null)
    /// and <c>published.release.ontology</c> (pinned release, version
    /// arrives in <c>request.ResourceId</c>). When <c>ResourceId</c> is
    /// empty the service picks the latest <c>ReleaseDeployment</c> row
    /// by <c>CreatedAt</c> (SQLite can't ORDER BY DateTimeOffset, so the
    /// service pulls the rows client-side and sorts in memory).
    /// </summary>
    Task<OntologyResponse?> GetPublishedAsync(InternalRequest request, CancellationToken cancellationToken);
}