using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Settings;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Application service for the three <c>settings.*</c> dispatcher arms
/// (12/13 slice): list_models / get / update. Delegates to the scoped
/// <see cref="SettingsService"/> and owns the snake_case wire
/// projections the pre-split dispatcher helpers performed inline.
/// Missing body throws <see cref="InvalidOperationException"/> exactly
/// like the pre-split helper.
/// </summary>
public sealed class SettingsApplicationService : ISettingsApplicationService
{
    private readonly SettingsService _settings;

    public SettingsApplicationService(SettingsService settings)
    {
        _settings = settings;
    }

    public Task<object?> ListModelsAsync(
        InternalRequest request, CancellationToken ct)
    {
        var row = _settings.ListModels();
        return Task.FromResult<object?>(ProjectModelCatalog(row));
    }

    public async Task<object?> GetAsync(
        InternalRequest request, CancellationToken ct)
    {
        var row = await _settings.GetAsync(ct).ConfigureAwait(false);
        return (object?)ProjectSettings(row);
    }

    public async Task<object?> UpdateAsync(
        InternalRequest request, CancellationToken ct)
    {
        var body = DeserializeBody<UpdateSettingsRequest>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for settings.update.");
        var row = await _settings.UpdateAsync(body, ct).ConfigureAwait(false);
        return (object?)ProjectSettings(row);
    }

    private static object ProjectSettings(SettingsOut row) => new
    {
        llm_provider_id = row.LlmProviderId,
        embedding_provider_id = row.EmbeddingProviderId,
        available_models = row.AvailableModels,
        temperature = row.Temperature,
        system_language = row.SystemLanguage,
        extract_model = row.ExtractModel,
    };

    private static object ProjectModelCatalog(ModelCatalogOut row) => new
    {
        models = row.Models,
        @default = row.Default,
    };
}
