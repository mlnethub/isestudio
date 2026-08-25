using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;

namespace ISEStudio.Controllers;

[ApiController]
public sealed class HealthController : ControllerBase
{
    private readonly ISEStudioOptions _options;

    public HealthController(IOptions<ISEStudioOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// Liveness/readiness probe. Mirrors the shape of the Python backend's
    /// <c>GET /api/health</c> so existing tooling keeps working during the
    /// .NET migration.
    /// </summary>
    [HttpGet("/api/health")]
    public object Get() => new
    {
        status = "ok",
        system_language = _options.SystemLanguage,
        extract_model = _options.ExtractModel,
        has_llm_key = !string.IsNullOrWhiteSpace(_options.LlmApiKey),
    };
}