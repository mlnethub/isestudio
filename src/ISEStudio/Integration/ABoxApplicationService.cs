using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Application.Ontology;
using ISEStudio.Ontology;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Default in-process implementation of <see cref="IABoxApplicationService"/>.
/// Each method unpacks one <see cref="InternalRequest"/> envelope
/// (path / query / body / actor), delegates to the underlying
/// <see cref="ABoxService"/>, and returns a strongly-typed DTO or
/// <c>null</c> when the domain service signals "no result".
/// <para>
/// <b>Important non-goals.</b> This service does not own the
/// transport-level fallback envelopes (<c>{classes:[],total:0}</c>,
/// <c>{iri:"",types:[]}</c>, <c>{removed_triples:0}</c>,
/// <c>{conforms:true,violations:[]}</c>, <c>{items:[],total:0}</c>,
/// <c>{revoked:Guid.Empty}</c>). Those wire shapes are pinned byte-for-byte
/// by <c>InternalApiContractTests</c> and must keep their anonymous
/// snake_case shape; the dispatcher arm still produces them when the app
/// service returns <c>null</c>. Likewise, the <c>409 job_id</c> envelope
/// lives on the switch-arm <c>RunWithExtractionGuardAsync</c> wrappers and
/// is not sunk into this layer in the pilot slice.
/// </para>
/// <para>
/// <b>Why null returns.</b> <see cref="ABoxService"/> declares each method
/// nullable (e.g. <c>Task&lt;ClassesOut?&gt;</c>) so it can signal
/// "I had no data to emit" without inventing typed fallbacks that would
/// silently drift from the anonymous wire shapes the contract pins.
/// </para>
/// </summary>
public sealed class ABoxApplicationService : IABoxApplicationService
{
    private readonly ABoxService _abox;

    public ABoxApplicationService(ABoxService abox)
    {
        ArgumentNullException.ThrowIfNull(abox);
        _abox = abox;
    }

    // ----------------------------------------------------------------------
    // IABoxApplicationService
    //
    // Envelope unpacking helpers (DeserializeBody<T> / ExtractIriFromBody /
    // QueryString / QueryInt) live in
    // <see cref="InternalRequestHelpers"/>; imported above via `using
    // static`. Pilot slice originally duplicated them inline here; the
    // 2026-08-28 cross-slice decision recorded in
    // docs/superpowers/specs/2026-08-28-abox-application-service-pilot.md
    // §6.1 promotes them to a single static class for the remaining 12
    // dispatcher slices.
    // ----------------------------------------------------------------------

    public Task<ClassesOut?> ListClassesAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null) return Task.FromResult<ClassesOut?>(null);
        return _abox.ListClassesAsync(
            request.KnowledgeSystemGuid.Value, request.Actor, cancellationToken);
    }

    public Task<IndividualsOut?> ListIndividualsAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null) return Task.FromResult<IndividualsOut?>(null);
        var classIri = QueryString(request, "class_iri");
        var q = QueryString(request, "q");
        var limit = QueryInt(request, "limit", 20);
        var offset = QueryInt(request, "offset", 0);
        return _abox.ListIndividualsAsync(
            request.KnowledgeSystemGuid.Value, classIri, q, limit, offset,
            request.Actor, cancellationToken);
    }

    public Task<IndividualOut?> GetIndividualAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null) return Task.FromResult<IndividualOut?>(null);
        var iri = QueryString(request, "iri");
        if (string.IsNullOrEmpty(iri)) return Task.FromResult<IndividualOut?>(null);
        return _abox.GetIndividualAsync(
            request.KnowledgeSystemGuid.Value, iri, request.Actor, cancellationToken);
    }

    public Task<IndividualOut?> CreateIndividualAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null || request.Body is null)
        {
            return Task.FromResult<IndividualOut?>(null);
        }
        var body = DeserializeBody<CreateIndividualRequest>(request);
        if (body is null) return Task.FromResult<IndividualOut?>(null);
        return _abox.CreateIndividualAsync(
            request.KnowledgeSystemGuid.Value, body, request.Actor, cancellationToken);
    }

    public Task<DeleteIndividualResponse?> DeleteIndividualAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null) return Task.FromResult<DeleteIndividualResponse?>(null);
        var iri = ExtractIriFromBody(request);
        if (string.IsNullOrEmpty(iri)) return Task.FromResult<DeleteIndividualResponse?>(null);
        return _abox.DeleteIndividualAsync(
            request.KnowledgeSystemGuid.Value, iri, request.Actor, cancellationToken);
    }

    public Task<IndividualOut?> AddAssertionAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null || request.Body is null)
        {
            return Task.FromResult<IndividualOut?>(null);
        }
        var body = DeserializeBody<AssertionRequest>(request);
        if (body is null) return Task.FromResult<IndividualOut?>(null);
        return _abox.AddAssertionAsync(
            request.KnowledgeSystemGuid.Value, body, request.Actor, cancellationToken);
    }

    public Task<IndividualOut?> RemoveAssertionAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null || request.Body is null)
        {
            return Task.FromResult<IndividualOut?>(null);
        }
        var body = DeserializeBody<AssertionRequest>(request);
        if (body is null) return Task.FromResult<IndividualOut?>(null);
        return _abox.RemoveAssertionAsync(
            request.KnowledgeSystemGuid.Value, body, request.Actor, cancellationToken);
    }

    public Task<ResetAboxResponse?> ResetAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null || request.Body is null)
        {
            return Task.FromResult<ResetAboxResponse?>(null);
        }
        var body = DeserializeBody<ResetAboxRequest>(request);
        if (body is null) return Task.FromResult<ResetAboxResponse?>(null);
        return _abox.ResetAsync(
            request.KnowledgeSystemGuid.Value, body, request.Actor, cancellationToken);
    }

    public Task<ValidationReportOut?> ValidateAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null) return Task.FromResult<ValidationReportOut?>(null);
        return _abox.ValidateAsync(
            request.KnowledgeSystemGuid.Value, request.Actor, cancellationToken);
    }

    public Task<ValidationReportOut?> FixViolationAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null || request.Body is null)
        {
            return Task.FromResult<ValidationReportOut?>(null);
        }
        var body = DeserializeBody<FixViolationRequest>(request);
        if (body is null) return Task.FromResult<ValidationReportOut?>(null);
        return _abox.FixViolationAsync(
            request.KnowledgeSystemGuid.Value, body, request.Actor, cancellationToken);
    }

    public Task<ValidationDecisionListOut?> ListValidationDecisionsAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null) return Task.FromResult<ValidationDecisionListOut?>(null);
        var q = QueryString(request, "q");
        var limit = QueryInt(request, "limit", 50);
        var offset = QueryInt(request, "offset", 0);
        return _abox.ListValidationDecisionsAsync(
            request.KnowledgeSystemGuid.Value, q, limit, offset,
            request.Actor, cancellationToken);
    }

    public Task<RevokeValidationDecisionResponse?> RevokeValidationDecisionAsync(InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || string.IsNullOrEmpty(request.ResourceId)
            || !Guid.TryParse(request.ResourceId, out var did))
        {
            return Task.FromResult<RevokeValidationDecisionResponse?>(null);
        }
        return _abox.RevokeValidationDecisionAsync(
            request.KnowledgeSystemGuid.Value, did, request.Actor, cancellationToken);
    }
}