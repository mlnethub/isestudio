using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Providers;

/// <summary>
/// Wire / persistence DTOs for the <c>/api/providers*</c> surface. Mirrors
/// the Python backend's <c>backend/app/api/providers.py</c> shape so the
/// existing frontend (SettingsPage.tsx) and contract tests keep working
/// without per-field adapters.
/// </summary>
/// <remarks>
/// <para>API keys are stored in <see cref="ProviderEntity.ApiKey"/> as plain
/// text (parity with the Python backend — see migration invariant "no
/// hardcoded admin credentials" for the rationale of sticking with the
/// proven format during the migration). The raw key is <strong>never</strong>
/// exposed on the wire; <see cref="ProviderOut"/> only surfaces
/// <see cref="ProviderOut.HasApiKey"/> + <see cref="ProviderOut.ApiKeyHint"/>
/// so logs and responses stay auditable.</para>
/// </remarks>
public static class ProviderDtos
{
    /// <summary>
    /// Mask an API key for display. Mirrors the Python backend's
    /// <c>_mask()</c> helper: <c>"••••" + last 4 characters</c> when the key
    /// is longer than 8 chars; <c>"••••"</c> otherwise. Returns
    /// <c>"••••"</c> for empty/null input so the UI never renders an empty
    /// hint cell.
    /// </summary>
    public static string MaskApiKey(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "••••";
        return raw.Length > 8 ? "••••" + raw[^4..] : "••••";
    }

    /// <summary>
    /// Map a <see cref="ProviderEntity"/> to its public wire shape.
    /// Callers MUST use this helper (not direct property assignment) so the
    /// masking rule is applied consistently.
    /// </summary>
    public static ProviderOut From(ProviderEntity entity) => new(
        Id: entity.Id,
        Name: entity.Name,
        Kind: entity.Kind,
        BaseUrl: entity.BaseUrl,
        Model: entity.Model,
        ConcurrencyLimit: entity.ConcurrencyLimit,
        HasApiKey: !string.IsNullOrEmpty(entity.ApiKey),
        ApiKeyHint: MaskApiKey(entity.ApiKey),
        LastTestOk: entity.LastTestOk,
        LastTestedAt: entity.LastTestedAt,
        CreatedAt: entity.CreatedAt);
}

/// <summary>
/// Public provider view. Snake-case on the wire (e.g. <c>api_key_hint</c>,
/// <c>last_test_ok</c>) thanks to the JSON naming policy wired in
/// <c>Program.cs</c>.
/// </summary>
public sealed record ProviderOut(
    Guid Id,
    string Name,
    string Kind,
    string BaseUrl,
    string Model,
    int ConcurrencyLimit,
    bool HasApiKey,
    string ApiKeyHint,
    bool? LastTestOk,
    DateTimeOffset? LastTestedAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// Body for <c>POST /api/providers</c>. <see cref="ApiKey"/> is required —
/// an empty key fails validation in <see cref="ProviderService.CreateAsync"/>.
/// </summary>
public sealed record ProviderCreateRequest(
    string Name,
    string Kind,
    string BaseUrl,
    string Model,
    string ApiKey,
    int ConcurrencyLimit);

/// <summary>
/// Body for <c>PATCH /api/providers/{id}</c>. Every field is optional so
/// the frontend can submit partial updates. The <see cref="ApiKey"/>
/// field has three states:
/// <list type="bullet">
///   <item><description><c>null</c> — field absent; no change.</description></item>
///   <item><description><c>""</c> — keep the existing key (the UI's
///   "leave blank to keep" hint).</description></item>
///   <item><description>non-empty — replace the stored key.</description></item>
/// </list>
/// </summary>
public sealed record ProviderPatchRequest(
    string? Name,
    string? Kind,
    string? BaseUrl,
    string? Model,
    string? ApiKey,
    int? ConcurrencyLimit);

/// <summary>
/// Body for <c>POST /api/providers/test</c>. When <see cref="ProviderId"/>
/// is supplied the service fills the blanks from the stored row; when
/// overrides are also supplied (the dialog "Test" button in
/// <c>SettingsPage.tsx</c> always does this), they take precedence. Mirrors
/// the Python <c>providers.py::test</c> endpoint semantics.
/// </summary>
public sealed record ProviderTestRequest(
    Guid? ProviderId,
    string? BaseUrl,
    string? ApiKey,
    string? Model,
    string? Kind);

/// <summary>
/// Response for <c>POST /api/providers/test</c>. <see cref="Message"/> is
/// a human-readable explanation (e.g. "Reached 47 models" or the surfaced
/// HTTP exception text). <see cref="LatencyMs"/> is the wall-clock millis
/// for the probe; surfaced so the operator can spot a slow endpoint at a
/// glance even when the test succeeded.
/// </summary>
public sealed record ProviderTestResult(
    bool Ok,
    string Message,
    long LatencyMs);