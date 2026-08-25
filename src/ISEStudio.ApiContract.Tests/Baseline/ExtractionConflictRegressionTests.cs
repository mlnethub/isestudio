using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.ApiContract.Tests.Baseline;

/// <summary>
/// Brief-mandated regression coverage for the "抽取进行中的修改返回 409"
/// path: an ontology edit that lands while an extraction job is in
/// flight for the bound knowledge system must surface HTTP 409 with the
/// <c>{"detail": { "error": "...", "job_id": "..." }}</c> envelope.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class ExtractionConflictRegressionTests
{
    /// <summary>
    /// Seeding a pending extraction row should make the next
    /// <c>ontology.edit</c> call return 409 Conflict instead of 200 OK.
    /// The dispatcher delegates the conflict to
    /// <see cref="ISEStudio.Api.FastApiErrorMiddleware"/>, which is the
    /// only place that converts the typed exception into the FastAPI
    /// envelope — both halves are exercised here so a future regression
    /// in either breaks the test.
    /// </summary>
    [Fact]
    public async Task Apply_ontology_changes_during_extraction_returns_409()
    {
        using var factory = new ApiContractWebApplicationFactory();
        var client = factory.CreateClient();

        // Seed an extraction job in the pending state. We exercise the
        // pending branch specifically (running is also covered by the
        // shared FindAnyActiveJobAsync query — both wire to 409 here).
        Guid jobId;
        Guid ksGuid;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            // The Testing environment opts out of the boot-time
            // EnsureCreated pass, so the schema has to be materialised
            // here before the seed insert can land.
            await db.Database.EnsureCreatedAsync();
            // The extraction job's FK to the knowledge system table is
            // enforced by SQLite, so we have to seed a parent row first
            // — the dispatcher's "any active job" query does not care
            // which KS it belongs to, so any Guid will do.
            var ks = new KnowledgeSystemEntity
            {
                PublicId = "test-ks",
                Name = "test",
                Description = string.Empty,
                GraphIri = "http://test/ks/1",
                BaseIri = "http://test/ks/1#",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.KnowledgeSystems.Add(ks);
            await db.SaveChangesAsync();
            ksGuid = ks.Id;
            var entity = new ExtractionJobEntity
            {
                KnowledgeSystemId = ks.Id,
                Kind = "tbox",
                Status = JobStatus.Pending.ToWire(),
                Model = "test-model",
                ChunkIds = new List<int>(),
                TotalChunks = 0,
                ProcessedChunks = 0,
                CreatedAt = DateTimeOffset.UtcNow,
                Log = string.Empty,
            };
            db.ExtractionJobs.Add(entity);
            await db.SaveChangesAsync();
            jobId = entity.Id;
        }

        // The controller route is {id:guid}; using "/1" would never reach
        // the dispatcher and we'd get a bare 404. Resolve the seeded KS's
        // actual Guid here.
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/knowledge/{ksGuid}/ontology/edit")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Authorization", $"ContractTest {Guid.NewGuid():N}");

        using var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict,
            $"Expected 409, got ({(int)response.StatusCode}) {response.StatusCode}. Body: {responseBody}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.ValueKind == JsonValueKind.Object, $"Expected object body, got '{body}' from response: {responseBody}");
        // The detail envelope carries both the human reason and the
        // job id the client should poll to learn when the extraction is
        // done. Asserting both keeps the wire shape locked to what the
        // brief documents.
        var detail = body.GetProperty("detail");
        Assert.True(detail.ValueKind == JsonValueKind.Object, $"Expected detail object, got '{detail}' from response: {responseBody}");
        Assert.True(true, $"DEBUG body={responseBody}");
        Assert.Contains("in progress", detail.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(jobId, detail.GetProperty("job_id").GetGuid());
    }
}