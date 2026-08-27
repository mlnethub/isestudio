using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Authentication;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Mcp;

namespace ISEStudio.ApiContract.Tests;

/// <summary>
/// Real-time authorization tests for the ISEStudio MCP transport.
/// Every test exercises the role / scope check from
/// <see cref="McpPrincipalAccessor"/> against a fresh sqlite seed, so
/// the brief-mandated "role not cached in token" guarantee is locked
/// down: a membership downgrade must take effect on the next MCP call
/// without invalidating the bearer token.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class McpAuthorizationTests
{
    /// <summary>
    /// Required verbatim test from the api-mcp plan. Demonstrates
    /// that:
    /// <list type="number">
    ///   <item>A token minted with <c>mcp:write</c> + editor role
    ///         successfully passes the write-tool role check.</item>
    ///   <item>After the user's grant is downgraded to viewer, the
    ///         same bearer token fails the same write-tool call with a
    ///         message that mentions "editor role".</item>
    /// </list>
    /// The bearer is never re-minted, so the only thing that changed
    /// is the live KS role lookup — proving the accessor does not
    /// cache role state. The test exercises the accessor directly so
    /// it stays decoupled from the JSON-RPC envelope the SDK uses on
    /// the wire (the envelope shape is owned by the SDK and can change
    /// between releases).
    /// </summary>
    [Fact]
    public async Task Existing_token_loses_write_access_after_membership_downgrade()
    {
        using var factory = new Baseline.ApiContractWebApplicationFactory();

        var (token, ks, user) = await SeedEditorAsync(factory, "test-ks-downgrade");

        // Step 1: preview succeeds with the editor grant. The accessor
        // resolves the principal live from the database, the role
        // check passes (editor >= editor), and the scope check passes
        // (the seeded token has mcp:read).
        await ResolveAndAssertEditorAsync(factory, token, expectRolePass: true);

        // Step 2: downgrade the membership. The KS grant row is the
        // only thing the live role lookup reads; flipping it to
        // "viewer" must be sufficient to demote the principal on the
        // next call.
        await DowngradeToViewerAsync(factory, user.Id, ks.Id);

        // Step 3: apply fails. The accessor raises
        // McpToolException("...editor role..."); the assertion below
        // pins the wording the api-mcp plan requires.
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => ResolveAndAssertEditorAsync(factory, token, expectRolePass: false));
        Assert.Contains("editor role", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A token minted without <c>mcp:write</c> cannot mutate state.
    /// The accessor raises <see cref="McpToolException"/> with the
    /// canonical wording; the SDK surfaces it as a JSON-RPC error
    /// body on the wire path. The test exercises the accessor
    /// directly so the wording stays pinned independent of any
    /// SDK envelope change.
    /// </summary>
    [Fact]
    public async Task Write_tool_requires_write_scope()
    {
        using var factory = new Baseline.ApiContractWebApplicationFactory();
        var (token, _, _) = await SeedEditorAsync(factory, "test-ks-scope", writeScope: false);

        // Resolve the principal then ask the accessor to enforce
        // the mcp:write scope. The accessor raises McpToolException
        // because the seeded token only carries mcp:read.
        using var scope = factory.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<McpPrincipalAccessor>();
        var ctx = new DefaultHttpContext
        {
            Items =
            {
                [McpPrincipalAccessor.PlaintextItemKey] = token,
            },
        };

        var principal = await accessor.ResolveAsync(ctx, CancellationToken.None);
        var ex = Assert.Throws<McpToolException>(() => accessor.RequireScope(principal, McpTokenScopes.McpWrite));
        Assert.Contains("mcp:write", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A POST without a bearer header is rejected with 401 and the
    /// FastAPI <c>{"detail": "..."}</c> envelope. The DNS-rebinding /
    /// bearer middleware is the only component that emits this shape
    /// for /mcp, so a regression there breaks the test.
    /// </summary>
    [Fact]
    public async Task Missing_bearer_returns_401()
    {
        using var factory = new Baseline.ApiContractWebApplicationFactory();
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"detail\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A POST with an unknown bearer is rejected with 401 too. The
    /// middleware hashes the plaintext against the
    /// <c>mcp_user_tokens</c> table; an unknown token produces a hash
    /// that does not match, so the request fails at the bearer check
    /// (never reaches the JSON-RPC handler). The test ensures the
    /// schema exists before issuing the request so the middleware's
    /// SELECT doesn't trip the sqlite "no such table" path on an
    /// empty database.
    /// </summary>
    [Fact]
    public async Task Unknown_bearer_returns_401()
    {
        using var factory = new Baseline.ApiContractWebApplicationFactory();
        var client = factory.CreateClient();

        // Materialise the schema so the middleware's SELECT against
        // mcpusertoken does not raise a "no such table" sqlite error.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", "Bearer opm_unknown_zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// HTTP-path downgrade coverage. The plan's required test
    /// (<see cref="Existing_token_loses_write_access_after_membership_downgrade"/>)
    /// exercises the accessor against a hand-built <see cref="DefaultHttpContext"/>,
    /// which is load-bearing but not wire-level. This test runs the
    /// downgrade scenario end-to-end through <c>POST /mcp</c>: the
    /// bearer middleware parses the header, the principal is
    /// re-resolved on each call (no caching), and the
    /// <c>apply_ontology_changes</c> tool body delegates the role
    /// check to <see cref="McpPrincipalAccessor.RequireRoleAsync"/>.
    ///
    /// <para>A regression where the middleware stops re-validating the
    /// user row between calls (or starts caching the resolved
    /// principal) would still pass <b>missing</b>/<b>unknown</b> bearer
    /// tests but trip here: the first call succeeds, then the grant is
    /// flipped to <c>viewer</c>, and the same bearer on a second
    /// <c>tools/call</c> must come back with a JSON-RPC error envelope
    /// (or an <c>isError</c> result body) carrying the
    /// <c>"editor role"</c> wording the plan pins.</para>
    /// </summary>
    [Fact]
    public async Task Http_path_downgrade_loses_write_access()
    {
        using var factory = new Baseline.ApiContractWebApplicationFactory();
        var (token, ks, user) = await SeedEditorAsync(factory, "test-ks-downgrade-http");
        var client = factory.CreateClient();

        // Step 1: tools/call apply_ontology_changes succeeds through
        // the wire path. The bearer middleware authenticates the
        // caller, the accessor resolves the live editor role, and the
        // tool body delegates to the dispatcher placeholder (which
        // returns null and the body returns new { ok = true }).
        var applyBody = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "apply_ontology_changes",
                arguments = new
                {
                    operations = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["op"] = "add_class",
                            ["label"] = "HttpDowngradeProbe",
                            ["comment"] = "downgrade probe",
                        },
                    },
                    confirm_destructive = true,
                    reason = "http-path downgrade probe",
                },
            },
        });

        var firstResponse = await PostMcpAsync(client, token, applyBody);
        var firstBody = await firstResponse.Content.ReadAsStringAsync();
        Assert.True(
            firstResponse.IsSuccessStatusCode,
            $"Expected 2xx for the editor call but got {(int)firstResponse.StatusCode}. Body: {firstBody}");

        // Step 2: flip the seeded grant to viewer. The accessor
        // re-reads the KS grant row on every tool call, so the very
        // next request must see the downgrade.
        await DowngradeToViewerAsync(factory, user.Id, ks.Id);

        // Step 3: same bearer, second tools/call. The role check
        // fails because the live lookup now returns viewer; the
        // accessor raises McpToolException("...editor role...") and
        // the SDK surfaces it via the JSON-RPC isError envelope. The
        // wording is what the plan pins.
        var secondResponse = await PostMcpAsync(client, token, applyBody);
        var secondBody = await secondResponse.Content.ReadAsStringAsync();
        Assert.Contains("editor role", secondBody, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Seed an owner user, a non-owner (grantee) user, a KS, an editor
    /// grant for the grantee, and an active MCP token bound to the
    /// grantee. Returns the bearer plaintext, the seeded KS entity,
    /// and the grantee user entity so the caller can mutate the
    /// grant.
    /// </summary>
    private static async Task<(string token, KnowledgeSystemEntity ks, UserEntity user)> SeedEditorAsync(
        Baseline.ApiContractWebApplicationFactory factory,
        string publicId,
        bool writeScope = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        await db.Database.EnsureCreatedAsync();

        var owner = new UserEntity
        {
            Username = $"owner-{publicId}",
            DisplayName = $"Owner {publicId}",
            IsAdmin = false,
            Active = true,
            PasswordHash = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(owner);

        var user = new UserEntity
        {
            Username = $"mcp-{publicId}",
            DisplayName = publicId,
            IsAdmin = false,
            Active = true,
            PasswordHash = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);

        var ks = new KnowledgeSystemEntity
        {
            PublicId = publicId,
            Name = publicId,
            Description = string.Empty,
            OwnerId = owner.Id,
            GraphIri = $"http://test/{publicId}",
            BaseIri = $"http://test/{publicId}#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);

        var grant = new KSGrantEntity
        {
            KnowledgeSystemId = ks.Id,
            UserId = user.Id,
            Role = "editor",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KSGrants.Add(grant);
        await db.SaveChangesAsync();

        // Mint a token via the service so the wire-format prefix and
        // SHA-256 digest match the production path.
        var tokens = scope.ServiceProvider.GetRequiredService<IMcpTokenService>();
        var scopes = new List<string> { McpTokenScopes.McpRead };
        if (writeScope) scopes.Add(McpTokenScopes.McpWrite);
        var minted = await tokens.CreateAsync(
            new McpTokenCreateRequest(
                KnowledgeSystemId: ks.Id,
                UserId: user.Id,
                Name: "test-token",
                Scopes: scopes,
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);
        return (minted.Plaintext, ks, user);
    }

    /// <summary>
    /// Flip the seeded grant to "viewer" so the next call exercises
    /// the downgrade path.
    /// </summary>
    private static async Task DowngradeToViewerAsync(
        Baseline.ApiContractWebApplicationFactory factory,
        Guid userId,
        Guid ksId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISEStudioDbContext>();
        var grant = await db.KSGrants.FirstOrDefaultAsync(
            g => g.UserId == userId && g.KnowledgeSystemId == ksId);
        Assert.NotNull(grant);
        grant!.Role = "viewer";
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Resolve the live principal for <paramref name="token"/>, then
    /// enforce the editor role check. When <paramref name="expectRolePass"/>
    /// is true the call must succeed; when false the accessor must
    /// raise <see cref="McpToolException"/> with the "editor role"
    /// wording the api-mcp plan pins.
    /// </summary>
    private static async Task ResolveAndAssertEditorAsync(
        Baseline.ApiContractWebApplicationFactory factory,
        string token,
        bool expectRolePass)
    {
        using var scope = factory.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<McpPrincipalAccessor>();
        var ctx = new DefaultHttpContext
        {
            Items =
            {
                [McpPrincipalAccessor.PlaintextItemKey] = token,
            },
        };
        var principal = await accessor.ResolveAsync(ctx, CancellationToken.None);
        if (expectRolePass)
        {
            await accessor.RequireRoleAsync(principal, KSRole.Editor, CancellationToken.None);
            return;
        }
        await accessor.RequireRoleAsync(principal, KSRole.Editor, CancellationToken.None);
    }

    /// <summary>
    /// POST a JSON-RPC envelope to <c>/mcp</c> with the supplied bearer.
    /// Returns the raw <see cref="HttpResponseMessage"/> so the caller
    /// can inspect both the status and the body (the SDK surfaces
    /// <see cref="McpToolException"/> as an <c>isError</c> result body
    /// that still carries 200 OK).
    /// </summary>
    private static async Task<HttpResponseMessage> PostMcpAsync(
        HttpClient client,
        string bearer,
        string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", $"Bearer {bearer}");
        // Streamable HTTP accepts both application/json and
        // text/event-stream on the response; the SDK picks whichever
        // matches the negotiated transport.
        request.Headers.Add("Accept", "application/json, text/event-stream");
        return await client.SendAsync(request).ConfigureAwait(false);
    }
}