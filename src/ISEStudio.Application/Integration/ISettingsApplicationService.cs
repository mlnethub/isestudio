using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-service seam for the three <c>settings.*</c> dispatcher
/// arms (12/13 slice): list_models / get / update. The implementation
/// resolves the scoped <c>SettingsService</c> through the constructor
/// and owns the wire projection (settings envelope + model catalog)
/// plus the missing-body throw semantics of the pre-split helpers.
///
/// <para><c>list_models</c> ignores the request envelope (it takes no
/// query parameters) but keeps the uniform signature so the dispatcher
/// wrapper stays single-shaped.</para>
/// </summary>
public interface ISettingsApplicationService
{
    /// <summary><c>settings.list_models</c> — model catalog for the frontend pickers.</summary>
    Task<object?> ListModelsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>settings.get</c> — current system-config wire envelope.</summary>
    Task<object?> GetAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>settings.update</c> — body <c>{llm_provider_id, embedding_provider_id, ...}</c>.</summary>
    Task<object?> UpdateAsync(InternalRequest request, CancellationToken cancellationToken);
}
