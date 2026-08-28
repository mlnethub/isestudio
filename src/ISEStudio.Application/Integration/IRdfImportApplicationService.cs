using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-service seam for the single <c>rdf.import</c> dispatcher
/// arm (13/13 slice — the last one, closing out the dispatcher
/// god-class split). The dispatcher keeps the
/// <see cref="InternalOperationDispatcher.RunWithExtractionGuardAsync"/>
/// 409 guard around the arm; this service owns the body unpacking
/// (multipart <c>file</c> bytes + form fields), the
/// <see cref="ISEStudio.Ontology.RdfImportService"/> call, and the
/// full snake_case wire projection.
///
/// <para>The return is <c>object?</c> because
/// <see cref="ISEStudio.Ontology.RdfImportResult"/> (and its
/// <see cref="ISEStudio.Ontology.OntologyResponse"/> /
/// <see cref="ISEStudio.Ontology.TerminologyResult"/> members) are
/// Infrastructure DTOs that cannot enter the zero-dependency
/// Application project. A missing <c>KnowledgeSystemGuid</c> throws
/// <see cref="InvalidOperationException"/> and a missing file throws
/// <see cref="ISEStudio.Ontology.RdfImportException"/> exactly like
/// the pre-split helper did; a missing body returns <c>null</c> so
/// the dispatcher degrades to its empty import envelope.</para>
/// </summary>
public interface IRdfImportApplicationService
{
    /// <summary><c>rdf.import</c> — multipart RDF import + full post-mutation pipeline projection.</summary>
    Task<object?> ImportAsync(InternalRequest request, CancellationToken cancellationToken);
}
