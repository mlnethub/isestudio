using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-service seam for the five <c>providers.*</c> dispatcher
/// arms (12/13 slice): list / create / update / delete / test. The
/// implementation resolves the scoped <c>ProviderService</c> through
/// the constructor and owns envelope unpacking (body DTOs, ResourceId
/// provider Guid) + throw semantics (missing body / invalid id →
/// <see cref="InvalidOperationException"/>, like the pre-split
/// helpers).
///
/// <para>Returns are <c>object?</c> because the wire DTOs
/// (<c>ProviderOut</c> / <c>ProviderTestResult</c>) live in the
/// Infrastructure slice. A <c>null</c> return degrades to the
/// dispatcher's schema-compatible fallback per arm.</para>
/// </summary>
public interface IProviderApplicationService
{
    /// <summary><c>providers.list</c> — all provider rows.</summary>
    Task<object?> ListAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>providers.create</c> — body <c>{name, kind, base_url, api_key, ...}</c>.</summary>
    Task<object?> CreateAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>providers.update</c> — provider Guid in <c>ResourceId</c>, patch body.</summary>
    Task<object?> UpdateAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>providers.delete</c> — provider Guid in <c>ResourceId</c>.</summary>
    Task<object?> DeleteAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>providers.test</c> — body <c>{kind, base_url, api_key, model}</c> probe.</summary>
    Task<object?> TestAsync(InternalRequest request, CancellationToken cancellationToken);
}
