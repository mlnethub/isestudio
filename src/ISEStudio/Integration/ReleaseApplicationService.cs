using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Application.Releases;
using ISEStudio.Exports;
using ISEStudio.Ontology;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Default in-process implementation of <see cref="IReleaseApplicationService"/>.
/// Each method unpacks one <see cref="InternalRequest"/> envelope
/// (path / query / body / actor) and delegates to the underlying domain
/// service. Twelve lifecycle ops go through <see cref="ReleaseService"/>;
/// four export ops go through <see cref="ExportService"/>.
/// <para>
/// <b>Important non-goals.</b> This service does not own the
/// transport-level fallback envelopes:
/// <list type="bullet">
/// <item><c>EmptyRelease()</c> for the nine lifecycle reads/writes;</item>
/// <item><c>EmptyReleaseDiff()</c> for <c>releases.diff</c>;</item>
/// <item><c>EmptyListResponse()</c> for <c>releases.list</c> /
/// <c>releases.list_exports</c>;</item>
/// <item><c>EmptyExportJob()</c> for the three <c>releases.*exports</c>
/// reads/writes;</item>
/// <item>inline <c>{ok:true}</c> for <c>releases.delete</c>;</item>
/// <item>inline <c>{restored:Guid.Empty, version:string.Empty}</c> for
/// <c>releases.rollback</c>;</item>
/// <item>inline <c>Array.Empty&lt;byte&gt;()</c> placeholder for
/// <c>releases.download_export_file</c> (unreachable; the middleware
/// short-circuits on <c>ExportFilePayloadException</c>).</item>
/// </list>
/// The dispatcher arm still produces those shapes when this service
/// returns <c>null</c>, matching the abox + conflicts + documents slice
/// decisions in
/// <c>docs/superpowers/specs/2026-08-28-abox-application-service-pilot.md</c>
/// §2.5.
/// </para>
/// <para>
/// <b>Special handling.</b>
/// <list type="bullet">
/// <item><see cref="RollbackAsync"/> returns the
/// <see cref="ReleaseRollbackResponse"/> envelope so the dispatcher
/// can pass it through verbatim. <c>ReleaseService.RollbackAsync</c>
/// already returns an anonymous <c>{restored, version}</c> shape; we
/// project it into the typed record.</item>
/// <item><see cref="CreateDraftAsync"/> reads <c>title</c> + <c>notes</c>
/// from the loose <c>"_"</c> envelope via
/// <see cref="InternalRequestHelpers.ReadStringField"/>. Both default
/// to <see cref="string.Empty"/> matching the Python defaults.</item>
/// <item><see cref="CreateExportAsync"/> builds an <see cref="ExportRequest"/>
/// from the loose body via <see cref="InternalRequestHelpers.ReadStringField"/>/
/// <see cref="InternalRequestHelpers.ReadIntField"/>. The dispatcher used
/// to inline these reads; they now live on the shared helpers so other
/// slices can adopt the same pattern.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ReleaseApplicationService : IReleaseApplicationService
{
    private readonly ReleaseService _releases;
    private readonly ExportService _exports;

    public ReleaseApplicationService(
        ReleaseService releases,
        ExportService exports)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(exports);
        _releases = releases;
        _exports = exports;
    }

    // ----------------------------------------------------------------------
    // Release lifecycle
    // ----------------------------------------------------------------------

    public Task<object?> ListAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return Task.FromResult<object?>(null);
        }
        return _releases.ListAsync(
            request.KnowledgeSystemGuid.Value, request.Actor, cancellationToken);
    }

    public Task<ReleaseOut?> CreateDraftAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return Task.FromResult<ReleaseOut?>(null);
        }
        var title = ReadStringField(request, "title") ?? string.Empty;
        var notes = ReadStringField(request, "notes") ?? string.Empty;
        return _releases.CreateDraftAsync(
            request.KnowledgeSystemGuid.Value, request.Actor, title, notes, cancellationToken);
    }

    public Task<ReleaseOut?> ReviewAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var releaseId))
        {
            return Task.FromResult<ReleaseOut?>(null);
        }
        var note = ReadStringField(request, "note");
        return _releases.ReviewAsync(
            request.KnowledgeSystemGuid.Value, releaseId, request.Actor, note, cancellationToken);
    }

    public Task<ReleaseOut?> PublishAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var releaseId))
        {
            return Task.FromResult<ReleaseOut?>(null);
        }
        var note = ReadStringField(request, "note");
        return _releases.PublishAsync(
            request.KnowledgeSystemGuid.Value, releaseId, request.Actor, note, cancellationToken);
    }

    public Task<ReleaseOut?> DeployAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var releaseId))
        {
            return Task.FromResult<ReleaseOut?>(null);
        }
        return _releases.DeployAsync(
            request.KnowledgeSystemGuid.Value, releaseId, request.Actor, cancellationToken);
    }

    public Task<ReleaseOut?> StopDeploymentAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var releaseId))
        {
            return Task.FromResult<ReleaseOut?>(null);
        }
        return _releases.StopDeploymentAsync(
            request.KnowledgeSystemGuid.Value, releaseId, request.Actor, cancellationToken);
    }

    public Task<ReleaseOut?> DeleteAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var releaseId))
        {
            return Task.FromResult<ReleaseOut?>(null);
        }
        return _releases.DeleteAsync(
            request.KnowledgeSystemGuid.Value, releaseId, request.Actor, cancellationToken);
    }

    public async Task<ReleaseRollbackResponse?> RollbackAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || !Guid.TryParse(request.ResourceId, out var releaseId))
        {
            return null;
        }
        // ReleaseService.RollbackAsync returns the typed
        // { restored, version } record (Guid + string). Pass it through.
        return await _releases.RollbackAsync(
            request.KnowledgeSystemGuid.Value, releaseId, request.Actor, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<object?> DiffAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return Task.FromResult<object?>(null);
        }
        var fromStr = QueryString(request, "from_id");
        var toStr = QueryString(request, "to_id");
        if (string.IsNullOrEmpty(fromStr) || string.IsNullOrEmpty(toStr)
            || !Guid.TryParse(fromStr, out var fromId)
            || !Guid.TryParse(toStr, out var toId))
        {
            return Task.FromResult<object?>(null);
        }
        // Pass through: ReleaseService.DiffAsync returns the anonymous
        // { from, to, layers } envelope. The layers dictionary is
        // free-form so we keep it as object? — the dispatcher
        // serialises it verbatim (the JSON property name policy handles
        // snake_case automatically for anonymous records).
        return _releases.DiffAsync(
            request.KnowledgeSystemGuid.Value, fromId, toId,
            request.Actor, cancellationToken);
    }

    // ----------------------------------------------------------------------
    // Release exports
    // ----------------------------------------------------------------------

    public Task<object?> ListExportsAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return Task.FromResult<object?>(null);
        }
        return _exports.ListAsync(
            request.KnowledgeSystemGuid.Value, cancellationToken);
    }

    public Task<ExportOut?> CreateExportAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return Task.FromResult<ExportOut?>(null);
        }
        // Body shape (ExportRequest): { layer, release_id?, shard_size? }
        // layer default "bundle"; release_id optional; shard_size
        // default 100_000. The dispatcher used to inline these reads;
        // they now live on the shared helpers so other slices can adopt
        // the same pattern.
        var layer = ReadStringField(request, "layer") ?? ExportLayer.Bundle;
        var releaseIdStr = ReadStringField(request, "release_id");
        Guid? releaseId = Guid.TryParse(releaseIdStr, out var rid) ? rid : null;
        var shardSize = ReadIntField(request, "shard_size") ?? 100_000;
        var body = new ExportRequest(layer, releaseId, shardSize);
        return _exports.CreateAsync(
            request.KnowledgeSystemGuid.Value, body, request.Actor, cancellationToken);
    }

    public Task<ExportOut?> GetExportAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || string.IsNullOrEmpty(request.ResourceId))
        {
            return Task.FromResult<ExportOut?>(null);
        }
        return _exports.GetAsync(
            request.KnowledgeSystemGuid.Value, request.ResourceId, cancellationToken);
    }

    public async Task DownloadExportFileAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || string.IsNullOrEmpty(request.ResourceId)
            || string.IsNullOrEmpty(request.SecondResourceId))
        {
            return;
        }
        // ExportService.DownloadFileAsync throws ExportFilePayloadException
        // — FastApiErrorMiddleware catches it and writes the raw-bytes
        // response. The await return value (placeholder bytes) is
        // unreachable in practice.
        await _exports.DownloadFileAsync(
            request.KnowledgeSystemGuid.Value,
            request.ResourceId,
            request.SecondResourceId,
            cancellationToken).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------
}