using ISEStudio.Application.Foundation;
using ISEStudio.Application.Ontology;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application service for the twelve <c>abox.*</c> operations the
/// internal REST contract exposes. Each method unpacks one
/// <see cref="InternalRequest"/> (path / query / body / actor), delegates
/// to the underlying domain service, and returns the strongly-typed DTO
/// the dispatcher should serialise — or <c>null</c> when the operation
/// has no body. The dispatcher keeps ownership of the
/// transport-level fallback envelopes (anonymous snake_case shapes that
/// the frozen OpenAPI contract pins verbatim) and the extraction-guard
/// <c>409 job_id</c> envelope, so this surface stays purely
/// operation-shape.
/// <para>
/// This is the pilot slice for the dispatcher-application-service split.
/// The pattern — <c>IIntegrationApiFacade.InvokeAsync(op, request, ct)</c>
/// → <see cref="IInternalOperationDispatcher"/> switch arm → this service
/// → domain service — should be replicated for the remaining
/// dispatcher-owned slices (<c>conflicts.*</c>, <c>documents.*</c>,
/// <c>vocabulary.*</c>, <c>releases.*</c>, <c>external.*</c>,
/// <c>published.*</c>, …) so the god-class switch eventually collapses
/// to one-line delegations. See
/// <c>docs/superpowers/specs/dispatcher-application-service-split.md</c>
/// for the rollout checklist.
/// </para>
/// </summary>
public interface IABoxApplicationService
{
    /// <summary><c>abox.list_classes</c> — TBox classes with per-class ABox count.</summary>
    Task<ClassesOut?> ListClassesAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>abox.list_individuals</c> — paginated ABox individual listing.</summary>
    Task<IndividualsOut?> ListIndividualsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>abox.get_individual</c> — full individual envelope (types + assertions).</summary>
    Task<IndividualOut?> GetIndividualAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>abox.create_individual</c> — declare a new individual. Body deserialised
    /// via <c>CreateIndividualRequest</c> under the loose <c>"_"</c> envelope key
    /// the dispatcher uses.
    /// </summary>
    Task<IndividualOut?> CreateIndividualAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>abox.delete_individual</c> — drop an individual. Returns <c>null</c>
    /// when the IRI is missing so the dispatcher can map to the empty-ref
    /// fallback; the IRI itself is sourced from
    /// <see cref="InternalRequest.Body"/> (Python baseline carries it
    /// there, not in the query).
    /// </summary>
    Task<DeleteIndividualResponse?> DeleteIndividualAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>abox.add_assertion</c> — attach a new object/data assertion.
    /// Body deserialised via <c>AssertionRequest</c>.
    /// </summary>
    Task<IndividualOut?> AddAssertionAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>abox.remove_assertion</c> — drop an object/data assertion.
    /// Body deserialised via <c>AssertionRequest</c>.
    /// </summary>
    Task<IndividualOut?> RemoveAssertionAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>abox.reset</c> — wipe the ABox for one knowledge system. Body
    /// deserialised via <c>ResetAboxRequest</c> when present; an absent body
    /// triggers the unconditional reset path.
    /// </summary>
    Task<ResetAboxResponse?> ResetAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>abox.validate</c> — run <c>ABoxValidator</c>, return violation report.</summary>
    Task<ValidationReportOut?> ValidateAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>abox.fix_violation</c> — apply one violation fix. Body deserialised
    /// via <c>FixViolationRequest</c>.
    /// </summary>
    Task<ValidationReportOut?> FixViolationAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>abox.list_validation_decisions</c> — page through persisted
    /// <c>ValidationDecision</c> rows, newest first.
    /// </summary>
    Task<ValidationDecisionListOut?> ListValidationDecisionsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>abox.revoke_validation_decision</c> — forget one
    /// <c>ValidationDecision</c> row. Returns <c>null</c> when no row
    /// matched so the dispatcher can map to 404.
    /// </summary>
    Task<RevokeValidationDecisionResponse?> RevokeValidationDecisionAsync(InternalRequest request, CancellationToken cancellationToken);
}