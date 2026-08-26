using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Api;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;

namespace ISEStudio.Providers;

/// <summary>
/// CRUD lifecycle for the <c>provider</c> table. Replaces the placeholder
/// <c>providers.*</c> cases in <see cref="Integration.InternalOperationDispatcher"/>
/// so <c>/api/providers*</c> reads and writes actually hit the database.
///
/// <para>Concurrency limits follow <see cref="Llm.LlmProviderConfig"/>'s
/// 1-64 range; the same bound shows up in the frontend's
/// <c>models.concurrencyRange</c> copy and the persistence configuration
/// column default. <see cref="Llm.LlmProviderConfig.MinConcurrencyLimit"/>
/// / <see cref="Llm.LlmProviderConfig.MaxConcurrencyLimit"/> are referenced
/// rather than duplicated so a future widening is a one-line change.</para>
///
/// <para>This service is the first dispatcher-routed CRUD in the project;
/// the dispatcher is a Singleton but the service is Scoped (it depends on
/// the scoped <see cref="ISEStudioDbContext"/>), so the dispatcher resolves
/// it per-request via <c>IServiceProvider</c> &mdash; see the
/// <c>InvokeProvider*Async</c> helpers in
/// <see cref="Integration.InternalOperationDispatcher"/>.</para>
/// </summary>
public sealed class ProviderService
{
    /// <summary>Canonical kind values; anything else is rejected at validation.</summary>
    public const string KindLlm = "llm";
    public const string KindEmbedding = "embedding";

    /// <summary>HTTP timeout for the probe in <see cref="TestAsync"/>.</summary>
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private readonly ISEStudioDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IHttpClientFactory _http;

    public ProviderService(
        ISEStudioDbContext db,
        TimeProvider clock,
        IHttpClientFactory http)
    {
        _db = db;
        _clock = clock;
        _http = http;
    }

    /// <summary>
    /// List every provider row ordered most-recent-first. Used by the
    /// settings UI to render the model-endpoints table.
    /// </summary>
    public async Task<IReadOnlyList<ProviderOut>> ListAsync(CancellationToken ct)
    {
        // Materialize first, then sort in-memory: SQLite (the
        // contract-test backend) can't translate ORDER BY over
        // DateTimeOffset columns. The rows count is bounded by the
        // number of model endpoints an operator has configured —
        // typically <10 — so a client-side sort is fine here.
        var rows = await _db.Providers
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        rows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return rows.ConvertAll(ProviderDtos.From);
    }

    /// <summary>
    /// Insert a new provider row. Validates that the kind is one of the
    /// canonical values, the API key is non-empty, and the concurrency
    /// limit is in the documented range. The persisted row is returned in
    /// wire shape (with the masked key) so the frontend can render it
    /// without a second round-trip.
    /// </summary>
    public async Task<ProviderOut> CreateAsync(ProviderCreateRequest req, CancellationToken ct)
    {
        ValidateCommon(req.Name, req.Kind, req.BaseUrl, req.Model, req.ConcurrencyLimit);
        if (string.IsNullOrEmpty(req.ApiKey))
        {
            throw new ValidationException("api_key is required when creating a provider.");
        }

        var entity = new ProviderEntity
        {
            Id = Guid.NewGuid(),
            Name = req.Name.Trim(),
            Kind = NormalizeKind(req.Kind),
            BaseUrl = req.BaseUrl.Trim(),
            Model = req.Model.Trim(),
            ApiKey = req.ApiKey,
            ConcurrencyLimit = req.ConcurrencyLimit,
            CreatedAt = _clock.GetUtcNow(),
        };
        // LegacyId is filled by the column DEFAULT 0 at INSERT time.
        _db.Providers.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ProviderDtos.From(entity);
    }

    /// <summary>
    /// Partial-update a provider. Three-valued <c>api_key</c> semantics:
    /// <c>null</c> = absent, <c>""</c> = keep existing, non-empty = replace.
    /// Aligns with the Python <c>backend/app/api/providers.py:106-107</c>
    /// rule that the frontend <c>"leave blank to keep"</c> copy relies on.
    /// </summary>
    public async Task<ProviderOut> UpdateAsync(Guid id, ProviderPatchRequest req, CancellationToken ct)
    {
        var entity = await _db.Providers
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            throw new InvalidOperationException($"Provider {id} not found.");
        }

        // Validate only the fields the caller supplied — missing fields
        // are intentionally allowed through.
        var newKind = req.Kind is null ? entity.Kind : NormalizeKind(req.Kind);
        var newName = req.Name?.Trim() ?? entity.Name;
        var newBaseUrl = req.BaseUrl?.Trim() ?? entity.BaseUrl;
        var newModel = req.Model?.Trim() ?? entity.Model;
        var newConcurrency = req.ConcurrencyLimit ?? entity.ConcurrencyLimit;
        ValidateCommon(newName, newKind, newBaseUrl, newModel, newConcurrency);

        entity.Name = newName;
        entity.Kind = newKind;
        entity.BaseUrl = newBaseUrl;
        entity.Model = newModel;
        entity.ConcurrencyLimit = newConcurrency;
        // Three-valued semantics: null => skip; "" => keep; non-empty => replace.
        if (req.ApiKey is not null && req.ApiKey.Length > 0)
        {
            entity.ApiKey = req.ApiKey;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ProviderDtos.From(entity);
    }

    /// <summary>
    /// Delete a provider by id. Refuses (409 territory) when the row is
    /// referenced as the LLM or embedding provider of any knowledge system
    /// or as the system default in <see cref="SystemConfigEntity"/>; the
    /// operator must clear the references first.
    /// </summary>
    /// <returns><c>true</c> when the row was removed, <c>false</c> when no
    /// row matched (caller can map to 404).</returns>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var referenced = await IsReferencedAsync(id, ct).ConfigureAwait(false);
        if (referenced)
        {
            // ResourceInUseException maps to HTTP 409 with a plain-string
            // {"detail": "..."} envelope (see FastApiErrorMiddleware); the
            // Python backend emits the same shape for the equivalent
            // provider-referenced-by-KS scenario.
            throw new ResourceInUseException(
                $"Provider {id} is referenced by a knowledge system or the system config; "
                + "clear those references before deleting.");
        }

        var entity = await _db.Providers
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            .ConfigureAwait(false);
        if (entity is null) return false;

        _db.Providers.Remove(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Probe an OpenAI-compatible endpoint with a <c>GET {base_url}/models</c>
    /// request. Mirrors the Python <c>providers.py::test</c> behavior:
    /// when <see cref="ProviderTestRequest.ProviderId"/> is supplied the
    /// stored row fills in the blanks; explicit overrides win. The probe
    /// result is persisted on the stored row so the UI's status icon
    /// reflects the most recent test, regardless of who triggered it.
    /// </summary>
    public async Task<ProviderTestResult> TestAsync(ProviderTestRequest req, CancellationToken ct)
    {
        // Resolve the effective test parameters from the stored row first,
        // then layer caller overrides on top — overrides win.
        ProviderEntity? stored = null;
        if (req.ProviderId is { } pid)
        {
            stored = await _db.Providers
                .FirstOrDefaultAsync(p => p.Id == pid, ct)
                .ConfigureAwait(false);
        }

        var baseUrl = req.BaseUrl?.Trim()
            ?? stored?.BaseUrl
            ?? throw new InvalidOperationException(
                "base_url is required when no provider_id is supplied.");
        var apiKey = req.ApiKey ?? stored?.ApiKey ?? string.Empty;
        var model = req.Model?.Trim() ?? stored?.Model ?? string.Empty;

        var (ok, message, latencyMs) = await ProbeAsync(baseUrl, apiKey, ct).ConfigureAwait(false);

        if (stored is not null)
        {
            stored.LastTestOk = ok;
            stored.LastTestedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new ProviderTestResult(ok, message, latencyMs);
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    /// True when any <see cref="KnowledgeSystemEntity"/> or the
    /// <see cref="SystemConfigEntity"/> singleton points at this provider
    /// as the LLM or embedding source. Both tables use
    /// <c>Restrict</c> on delete (see <c>EntityConfigurations.cs</c>) so
    /// the EF Core DELETE would fail anyway; we surface a friendly message
    /// first instead of letting the FK exception leak through.
    /// </summary>
    private async Task<bool> IsReferencedAsync(Guid providerId, CancellationToken ct)
    {
        var ksHit = await _db.KnowledgeSystems
            .AsNoTracking()
            .Where(k => k.LlmProviderId == providerId || k.EmbeddingProviderId == providerId)
            .Select(k => k.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (ksHit != Guid.Empty) return true;

        var sysHit = await _db.SystemConfigs
            .AsNoTracking()
            .Where(s => s.LlmProviderId == providerId || s.EmbeddingProviderId == providerId)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return sysHit != Guid.Empty;
    }

    /// <summary>
    /// GET <c>{baseUrl}/models</c> with a 5-second ceiling. The probe is
    /// cheap (no chat tokens consumed) and every OpenAI-compatible gateway
    /// we ship against &mdash; OpenAI, OpenRouter, DeepSeek, Ollama, Azure
    /// OpenAI &mdash; exposes it. The "model_count" in the success message
    /// gives operators a fast sanity check that the returned list isn't
    /// empty (some misconfigured endpoints return <c>{"data":[]}</c>).
    /// </summary>
    private async Task<(bool Ok, string Message, long LatencyMs)> ProbeAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/models";
        var client = _http.CreateClient(nameof(ProviderService));
        client.Timeout = TestTimeout;

        if (!string.IsNullOrEmpty(apiKey))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            sw.Stop();
            if (!response.IsSuccessStatusCode)
            {
                return (false,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim(),
                    sw.ElapsedMilliseconds);
            }
            // Count the data entries so the success message is concrete;
            // fall back to a generic phrasing if the body shape is unexpected.
            int count;
            try
            {
                var doc = await response.Content.ReadFromJsonAsync<ModelsListShape>(cancellationToken: ct)
                    .ConfigureAwait(false);
                count = doc?.Data?.Length ?? 0;
            }
            catch
            {
                count = -1;
            }
            var msg = count >= 0
                ? $"Reached {count} model{(count == 1 ? string.Empty : "s")}."
                : "Endpoint returned a 2xx response.";
            return (true, msg, sw.ElapsedMilliseconds);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return (false, $"Timed out after {TestTimeout.TotalSeconds:0}s.", sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return (false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Minimal DTO for the OpenAI <c>/models</c> response shape so we can
    /// peek at <c>data</c> without dragging in the full OpenAI SDK.
    /// </summary>
    private sealed record ModelsListShape(ModelEntry[]? Data);
    private sealed record ModelEntry(string? Id);

    /// <summary>
    /// Validate the field set shared by create and update. The dispatcher
    /// maps <see cref="Api.ValidationException"/> to an HTTP 400 envelope via
    /// <see cref="Api.FastApiErrorMiddleware"/>; the error messages are
    /// intentionally human-readable because the dialog surfaces them
    /// verbatim through Sonner toasts.
    /// </summary>
    private static void ValidateCommon(string name, string kind, string baseUrl, string model, int concurrency)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationException("name is required.");
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ValidationException("base_url is required.");
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ValidationException("model is required.");
        }
        if (concurrency < Llm.LlmProviderConfig.MinConcurrencyLimit
            || concurrency > Llm.LlmProviderConfig.MaxConcurrencyLimit)
        {
            throw new ValidationException(
                $"concurrency_limit must be between {Llm.LlmProviderConfig.MinConcurrencyLimit} "
                + $"and {Llm.LlmProviderConfig.MaxConcurrencyLimit}.");
        }
    }

    /// <summary>
    /// Normalize the kind string. The Python backend accepts arbitrary
    /// values and falls back to <c>"llm"</c>; we are stricter and reject
    /// anything that isn't <c>llm</c> or <c>embedding</c> so an admin
    /// typo doesn't silently disable a kind-specific code path.
    /// </summary>
    private static string NormalizeKind(string kind)
    {
        var k = kind.Trim().ToLowerInvariant();
        return k switch
        {
            KindLlm => KindLlm,
            KindEmbedding => KindEmbedding,
            _ => throw new ValidationException(
                $"kind must be '{KindLlm}' or '{KindEmbedding}'."),
        };
    }
}