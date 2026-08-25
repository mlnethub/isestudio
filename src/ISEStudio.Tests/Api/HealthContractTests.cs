using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ISEStudio.Tests.Api;

public sealed class HealthContractTests
{
    [Fact]
    public async Task Health_uses_the_existing_route_and_shape()
    {
        await using var app = new ISEStudioWebApplicationFactory();
        var response = await app.CreateClient().GetAsync("/api/health");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("system_language", out _));
        Assert.True(json.TryGetProperty("extract_model", out _));
        Assert.True(json.TryGetProperty("has_llm_key", out _));
    }
}