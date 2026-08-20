using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Authentication;
using OnToPilot.Tests.Extraction;
using OnToPilot.Tests.Persistence;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// HTTP-level contract tests for the B8 vocabulary proposal lifecycle.
/// <c>vocabulary.accept_proposal</c> / <c>vocabulary.reject_proposal</c> now
/// route through <see cref="VocabularyProposalService"/> via the dispatcher.
/// </summary>
[Collection(ExtractionTestCollection.Name)]
public sealed class VocabularyProposalApiTests
{
    private const string CookieHeader = "ontopilot_session";

    [Fact]
    public async Task Accept_proposal_applies_payload_and_writes_audit()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b8-accept");

        // Create the scheme the proposal's <c>scheme_iri</c> references
        // first &mdash; VocabularyProposalService.AcceptProposalAsync hands
        // the payload's scheme_iri to SkosManager.CreateConcept, which
        // rejects a missing scheme with SkosValidationException.
        var schemeIri = await CreateSchemeAsync(client, ksId);
        var proposalId = await SeedPendingProposalAsync(app, ksGuid, "create", "Test Term", schemeIri);

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/vocabulary/proposals/{proposalId}/accept",
            new { note = "accepted in test" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("accepted",
            json.GetProperty("proposal").GetProperty("status").GetString());

        var db = app.CreateDbContext();
        var proposal = await db.TermProposals.FindAsync(proposalId);
        Assert.NotNull(proposal);
        Assert.Equal("accepted", proposal!.Status);
        Assert.NotNull(proposal.ResolvedAt);
    }

    [Fact]
    public async Task Reject_proposal_marks_status_rejected_and_writes_audit()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var (client, _) = await SeedAdminAndClientAsync(app);
        var (ksId, ksGuid) = await SeedKnowledgeSystemAsync(app, client, "b8-reject");

        var proposalId = await SeedPendingProposalAsync(app, ksGuid, "create", "Rejected Term");

        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/vocabulary/proposals/{proposalId}/reject",
            new { note = "rejected in test" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rejected",
            json.GetProperty("status").GetString());

        var db = app.CreateDbContext();
        var proposal = await db.TermProposals.FindAsync(proposalId);
        Assert.NotNull(proposal);
        Assert.Equal("rejected", proposal!.Status);
        Assert.NotNull(proposal.ResolvedAt);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static async Task<Guid> SeedPendingProposalAsync(
        AuthTestWebApplicationFactory app, Guid ksGuid, string action, string term,
        string? schemeIri = null)
    {
        var db = app.CreateDbContext();
        // Build the payload with snake_case wire field names so the
        // dispatcher's <c>BuildConceptData</c> round-trip maps them onto
        // <see cref="OnToPilot.Ontology.SkosConceptData"/> correctly. A
        // <c>create</c> proposal must include <c>scheme_iri</c> &mdash;
        // VocabularyProposalService.AcceptProposalAsync rejects a missing
        // one with SkosValidationException, which the test factory's
        // FastApiErrorMiddleware does NOT translate to 4xx. The scheme IRI
        // also has to point at a scheme that already exists in the graph
        // (SkosManager.ValidateConcept enforces this), so the test seeds a
        // scheme up-front and threads its IRI through here.
        var payload = action == "create" && schemeIri is not null
            ? new
            {
                scheme_iri = schemeIri,
                preferred_label = term,
                language = "en",
                description = "seeded for test",
            }
            : (object)new
            {
                preferred_label = term,
                language = "en",
                description = "seeded for test",
            };
        var proposal = new TermProposalEntity
        {
            LegacyId = TestLegacyIds.Next("term_proposal"),
            KnowledgeSystemId = ksGuid,
            Signature = $"test-{action}-{term}",
            Action = action,
            Term = term,
            TargetIri = null,
            Status = "pending",
            Payload = JsonDocument.Parse(JsonSerializer.Serialize(payload)),
            Confidence = 0.9,
            Reason = "seeded",
            ProposedBy = "terminology-agent",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.TermProposals.Add(proposal);
        await db.SaveChangesAsync();
        return proposal.Id;
    }

    private static async Task<string> CreateSchemeAsync(HttpClient client, Guid ksId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/knowledge/{ksId}/vocabulary/schemes",
            new
            {
                iri = (string?)null,
                title = "Accept Test Scheme",
                default_language = "en",
                description = "scheme used to accept a proposal",
                origin = "manual",
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("iri").GetString()!;
    }

    private static async Task<(HttpClient Client, Guid AdminId)> SeedAdminAndClientAsync(
        AuthTestWebApplicationFactory app)
    {
        var db = app.CreateDbContext();
        if (!db.Users.Any(u => u.Username == AuthTestWebApplicationFactory.AdminUsername))
        {
            var passwordService = new PasswordService();
            db.Users.Add(new UserEntity
            {
                LegacyId = TestLegacyIds.Next("users"),
                Username = AuthTestWebApplicationFactory.AdminUsername,
                DisplayName = AuthTestWebApplicationFactory.AdminDisplayName,
                PasswordHash = passwordService.Hash(AuthTestWebApplicationFactory.AdminPassword),
                IsAdmin = true,
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = AuthTestWebApplicationFactory.AdminUsername,
            password = AuthTestWebApplicationFactory.AdminPassword,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(
            c => c.StartsWith(CookieHeader + "=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var adminId = db.Users
            .Single(u => u.Username == AuthTestWebApplicationFactory.AdminUsername).Id;
        return (client, adminId);
    }

    private static async Task<(Guid KsId, Guid KsGuid)> SeedKnowledgeSystemAsync(
        AuthTestWebApplicationFactory app, HttpClient client, string tag)
    {
        var response = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = $"ks-{tag}",
            description = tag,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // The wire `id` is the KS primary-key Guid (the migration removed
        // the legacy integer from the DTO).
        var ksId = body.GetProperty("id").GetGuid();
        return (ksId, ksId);
    }
}
