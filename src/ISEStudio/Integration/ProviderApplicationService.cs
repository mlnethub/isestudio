using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Providers;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Application service for the five <c>providers.*</c> dispatcher arms
/// (12/13 slice). Unpacks the <see cref="InternalRequest"/> envelope
/// (body DTOs + ResourceId provider Guid), delegates to
/// <see cref="ProviderService"/>, and returns the wire DTO. Missing
/// body / invalid provider id throw <see cref="InvalidOperationException"/>
/// exactly like the pre-split helpers (→ FastApiErrorMiddleware
/// <c>{"detail": ...}</c> envelope).
/// </summary>
public sealed class ProviderApplicationService : IProviderApplicationService
{
    private readonly ProviderService _providers;

    public ProviderApplicationService(ProviderService providers)
    {
        _providers = providers;
    }

    public async Task<object?> ListAsync(
        InternalRequest request, CancellationToken ct)
    {
        var rows = await _providers.ListAsync(ct).ConfigureAwait(false);
        return (object?)rows;
    }

    public async Task<object?> CreateAsync(
        InternalRequest request, CancellationToken ct)
    {
        var body = DeserializeBody<ProviderCreateRequest>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for providers.create.");
        var row = await _providers.CreateAsync(body, ct).ConfigureAwait(false);
        return (object?)row;
    }

    public async Task<object?> UpdateAsync(
        InternalRequest request, CancellationToken ct)
    {
        var id = Guid.TryParse(request.ResourceId, out var parsed) ? parsed : Guid.Empty;
        if (id == Guid.Empty)
        {
            throw new InvalidOperationException("provider id must be a valid UUID.");
        }
        var body = DeserializeBody<ProviderPatchRequest>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for providers.update.");
        var row = await _providers.UpdateAsync(id, body, ct).ConfigureAwait(false);
        return (object?)row;
    }

    public async Task<object?> DeleteAsync(
        InternalRequest request, CancellationToken ct)
    {
        var id = Guid.TryParse(request.ResourceId, out var parsed) ? parsed : Guid.Empty;
        if (id == Guid.Empty)
        {
            throw new InvalidOperationException("provider id must be a valid UUID.");
        }
        var removed = await _providers.DeleteAsync(id, ct).ConfigureAwait(false);
        return (object?)new { deleted = removed ? 1 : 0 };
    }

    public async Task<object?> TestAsync(
        InternalRequest request, CancellationToken ct)
    {
        var body = DeserializeBody<ProviderTestRequest>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for providers.test.");
        var result = await _providers.TestAsync(body, ct).ConfigureAwait(false);
        return (object?)result;
    }
}
