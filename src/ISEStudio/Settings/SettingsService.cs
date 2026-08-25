using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ISEStudio.Api;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Providers;

namespace ISEStudio.Settings;

// ---------------------------------------------------------------------------
// Wire DTOs for /api/settings + /api/models. Mirrors
// backend/app/api/settings_api.py so the existing frontend
// SettingsOut / ModelsList types stay in lock-step with the Python
// baseline.
// ---------------------------------------------------------------------------

/// <summary>
/// The admin's global settings payload. Mirrors
/// <c>backend/app/api/settings_api.py:SettingsOut</c>.
/// </summary>
public sealed record SettingsOut(
    Guid? LlmProviderId,
    Guid? EmbeddingProviderId,
    IReadOnlyList<string> AvailableModels,
    double Temperature,
    string SystemLanguage,
    string ExtractModel);

/// <summary>
/// Body for <c>PUT /api/settings</c>. Mirrors
/// <c>backend/app/api/settings_api.py:SettingsUpdate</c>: each field is
/// three-valued (omit = unchanged; null in body = unchanged; non-null =
/// replace).
/// </summary>
public sealed record UpdateSettingsRequest(
    Guid? LlmProviderId,
    Guid? EmbeddingProviderId);

/// <summary>
/// Read-only model catalog used by the picker UI. Mirrors
/// <c>backend/app/api/settings_api.py:list_models</c>.
/// </summary>
public sealed record ModelCatalogOut(
    IReadOnlyList<string> Models,
    string Default);

/// <summary>
/// Settings CRUD. Replaces the placeholder <c>settings.*</c> cases in
/// <see>ISEStudio.Integration.InternalOperationDispatcher</see> so the
/// admin <c>/api/settings</c> surface reads and writes the singleton
/// <see cref="SystemConfigEntity"/> + the .env-managed temperature.
///
/// <para>The Python baseline keeps the temperature in the .env
/// (<c>settings.llm_temperature</c>) — read-only on the wire. The
/// available-models list is also .env-driven
/// (<c>ISEStudio:Llm:ModelChoices</c>, comma-separated); the C# service
/// pre-pends the active <see cref="ISEStudio.Configuration.ISEStudioOptions.ExtractModel"/>
/// so the picker always offers the default even when the operator
/// didn't list it explicitly, mirroring
/// <c>backend/app/model_config.py:available_models</c>.</para>
/// </summary>
public sealed class SettingsService
{
    private readonly ISEStudioDbContext _db;
    private readonly IConfiguration _config;
    private readonly ISEStudio.Configuration.ISEStudioOptions _options;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Configuration key for the .env-managed temperature. Mirrors the
    /// Python backend's <c>llm_temperature</c>.
    /// </summary>
    public const string TemperatureConfigKey = "ISEStudio:Llm:Temperature";

    /// <summary>
    /// Configuration key for the operator-supplied model-name suggestions
    /// (comma-separated). Mirrors the Python backend's
    /// <c>llm_model_choices</c>.
    /// </summary>
    public const string ModelChoicesConfigKey = "ISEStudio:Llm:ModelChoices";

    public SettingsService(
        ISEStudioDbContext db,
        IConfiguration config,
        Microsoft.Extensions.Options.IOptions<ISEStudio.Configuration.ISEStudioOptions> options,
        TimeProvider clock)
    {
        _db = db;
        _config = config;
        _options = options.Value;
        _clock = clock;
    }

    /// <summary>
    /// Read the singleton system config row, materialising it if missing
    /// (the Python baseline <c>get_system_config</c> does the same:
    /// inserts a fresh <c>SystemConfig(id=1)</c> row on first read).
    /// </summary>
    public async Task<SystemConfigEntity> GetOrCreateSystemConfigAsync(
        CancellationToken ct)
    {
        // The C# schema keeps the singleton under LegacyId == 1 (matches
        // Python SystemConfig.id == 1) so the two implementations agree on
        // the row identity even though the C# side uses a Guid primary
        // key + LegacyAddressableEntity.LegacyId.
        var cfg = await _db.SystemConfigs
            .FirstOrDefaultAsync(s => s.LegacyId == SystemConfigEntity.SingletonLegacyId, ct)
            .ConfigureAwait(false);
        if (cfg is null)
        {
            cfg = new SystemConfigEntity
            {
                Id = Guid.NewGuid(),
                LegacyId = SystemConfigEntity.SingletonLegacyId,
                UpdatedAt = _clock.GetUtcNow(),
            };
            _db.SystemConfigs.Add(cfg);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        return cfg;
    }

    /// <summary>
    /// Project the current settings to the wire shape. Mirrors
    /// <c>backend/app/api/settings_api.py:_payload</c>.
    /// </summary>
    public async Task<SettingsOut> GetAsync(CancellationToken ct)
    {
        var cfg = await GetOrCreateSystemConfigAsync(ct).ConfigureAwait(false);
        return ProjectSettings(cfg);
    }

    /// <summary>
    /// Update the system-default provider pointers. Mirrors
    /// <c>backend/app/api/settings_api.py:update_settings</c>: each
    /// provider id is validated against the <see cref="ProviderEntity"/>
    /// table, and the kind check rejects an LLM pointer that points at an
    /// embedding row (and vice versa). Persists the row + bumps
    /// <c>UpdatedAt</c>.
    /// </summary>
    public async Task<SettingsOut> UpdateAsync(
        UpdateSettingsRequest body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var cfg = await GetOrCreateSystemConfigAsync(ct).ConfigureAwait(false);

        if (body.LlmProviderId is { } llmId)
        {
            await RequireProviderAsync(llmId, ProviderService.KindLlm, ct)
                .ConfigureAwait(false);
            cfg.LlmProviderId = llmId;
        }
        if (body.EmbeddingProviderId is { } embId)
        {
            await RequireProviderAsync(embId, ProviderService.KindEmbedding, ct)
                .ConfigureAwait(false);
            cfg.EmbeddingProviderId = embId;
        }
        cfg.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // refresh_runtime (Python) reloads the in-process endpoint cache
        // so the next LLM call uses the new default. The C# ChatClient
        // / EmbeddingGenerator factories read the live IConfiguration +
        // DbContext per call, so no explicit cache invalidation is needed
        // here — the next request resolves the new default provider
        // naturally.

        return ProjectSettings(cfg);
    }

    /// <summary>
    /// Build the model-name catalog. Mirrors
    /// <c>backend/app/api/settings_api.py:list_models</c>: the operator's
    /// .env-supplied choices + the active extract-model prepended so the
    /// picker always offers the default. The .env default is returned
    /// alongside so the frontend can flag the recommended entry.
    /// </summary>
    public ModelCatalogOut ListModels()
    {
        var choicesRaw = _config[ModelChoicesConfigKey] ?? string.Empty;
        var choices = choicesRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!choices.Contains(_options.ExtractModel, StringComparer.Ordinal))
        {
            choices.Insert(0, _options.ExtractModel);
        }
        return new ModelCatalogOut(choices, _options.ExtractModel);
    }

    // ---- helpers ----------------------------------------------------------

    private async Task RequireProviderAsync(Guid id, string kind, CancellationToken ct)
    {
        var provider = await _db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            .ConfigureAwait(false);
        if (provider is null)
        {
            throw new ValidationException($"Model entry {id} not found.");
        }
        if (!string.Equals(provider.Kind, kind, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException(
                $"Entry {id} is a {provider.Kind} entry, not {kind}.");
        }
    }

    private SettingsOut ProjectSettings(SystemConfigEntity cfg) => new(
        LlmProviderId: cfg.LlmProviderId,
        EmbeddingProviderId: cfg.EmbeddingProviderId,
        AvailableModels: ListModels().Models,
        Temperature: _config.GetValue<double?>(TemperatureConfigKey) ?? 0.0,
        SystemLanguage: _options.SystemLanguage,
        ExtractModel: _options.ExtractModel);
}