using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Sparql;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;
using ISEStudio.Sparql;
using ISEStudio.Tests.Authentication;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Oxigraph;
using Xunit;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Sparql;

[Collection(nameof(ExtractionTestCollection))]
public sealed class SparqlQueryExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_unknown_public_id_throws_KeyNotFound()
    {
        await using var app = new AuthTestWebApplicationFactory();
        using var scope = app.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISparqlQueryExecutor>();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            executor.ExecuteAsync("does-not-exist", "SELECT * WHERE { ?s ?p ?o } LIMIT 1",
                100, MakeToken("any"), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_empty_query_throws_ValidationException()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "sparql-empty");
        using var scope = app.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISparqlQueryExecutor>();
        await Assert.ThrowsAsync<ISEStudio.Api.ValidationException>(() =>
            executor.ExecuteAsync(ks.PublicId, "  ", 100, MakeToken("u"), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_select_returns_row_per_binding()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "sparql-select");
        await SeedTurtleAsync(app, ks,
            "<http://ex/s> <http://ex/p> <http://ex/o1>, <http://ex/o2> .");

        using var scope = app.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISparqlQueryExecutor>();
        var res = await executor.ExecuteAsync(
            ks.PublicId,
            "SELECT ?o WHERE { <http://ex/s> <http://ex/p> ?o } LIMIT 10",
            100, MakeToken("u"), CancellationToken.None);

        Assert.Equal(2, res.Rows.Count);
        var objects = res.Rows.Select(r => r["o"]).ToHashSet();
        Assert.Contains("http://ex/o1", objects);
        Assert.Contains("http://ex/o2", objects);
    }

    [Fact]
    public async Task ExecuteAsync_ask_returns_single_boolean_row()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "sparql-ask");
        await SeedTurtleAsync(app, ks, "<http://ex/s> <http://ex/p> <http://ex/o> .");

        using var scope = app.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISparqlQueryExecutor>();
        var res = await executor.ExecuteAsync(
            ks.PublicId,
            "ASK WHERE { <http://ex/s> <http://ex/p> <http://ex/o> }",
            100, MakeToken("u"), CancellationToken.None);

        Assert.Single(res.Rows);
        Assert.True((bool)res.Rows[0]["boolean"]!);
    }

    [Fact]
    public async Task ExecuteAsync_limits_rows_when_sparql_has_no_limit()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "sparql-limit");
        await SeedTurtleAsync(app, ks,
            string.Join(" ", Enumerable.Range(0, 20)
                .Select(i => $"<http://ex/s{i}> <http://ex/p> <http://ex/o{i}> .")));

        using var scope = app.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISparqlQueryExecutor>();
        var res = await executor.ExecuteAsync(
            ks.PublicId,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o }",  // no LIMIT
            5, MakeToken("u"), CancellationToken.None);

        Assert.Equal(5, res.Rows.Count);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_leak_other_ks_graphs()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ksA = await SeedKsAsync(app, "sparql-leak-a");
        var ksB = await SeedKsAsync(app, "sparql-leak-b");
        await SeedTurtleAsync(app, ksA, "<http://ex/secret> <http://ex/p> <http://ex/in-a> .");
        await SeedTurtleAsync(app, ksB, "<http://ex/secret> <http://ex/p> <http://ex/in-b> .");

        using var scope = app.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISparqlQueryExecutor>();
        var res = await executor.ExecuteAsync(
            ksA.PublicId,
            "SELECT ?o WHERE { <http://ex/secret> <http://ex/p> ?o } LIMIT 10",
            100, MakeToken("u"), CancellationToken.None);

        var objects = res.Rows.Select(r => r["o"]).ToHashSet();
        Assert.Contains("http://ex/in-a", objects);
        Assert.DoesNotContain("http://ex/in-b", objects);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_construct_query_via_policy()
    {
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "sparql-reject");
        using var scope = app.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISparqlQueryExecutor>();
        await Assert.ThrowsAsync<ISEStudio.Api.ValidationException>(() =>
            executor.ExecuteAsync(ks.PublicId,
                "CONSTRUCT WHERE { ?s ?p ?o } LIMIT 5",
                100, MakeToken("u"), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_select_returns_string_literal_binding()
    {
        // Regression guard: ProjectTerm previously NRE'd on a plain
        // literal (null Datatype) → HTTP 500. A SELECT that binds an
        // object literal exercises the same projection path the
        // contract-test demo graph (rdfs:label "...") hits, pinning the
        // fix in place at the service level.
        await using var app = new AuthTestWebApplicationFactory();
        var ks = await SeedKsAsync(app, "sparql-literal");
        await SeedTurtleAsync(app, ks,
            "<http://ex/s> <http://ex/label> \"Rex\" .");

        using var scope = app.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISparqlQueryExecutor>();
        var res = await executor.ExecuteAsync(
            ks.PublicId,
            "SELECT ?l WHERE { <http://ex/s> <http://ex/label> ?l } LIMIT 5",
            100, MakeToken("u"), CancellationToken.None);

        Assert.Single(res.Rows);
        Assert.Equal("Rex", res.Rows[0]["l"]);
    }

    // --- helpers ---

    private static TokenPrincipal MakeToken(string userId) =>
        new(userId, "any", Array.Empty<string>());

    private static async Task<KnowledgeSystemEntity> SeedKsAsync(
        AuthTestWebApplicationFactory app, string tag)
    {
        var db = app.CreateDbContext();
        // Seed a minimal admin user so the KS owner FK resolves. The
        // SPARQL executor never reads the user table — only PublicId +
        // GraphIri + BaseIri matter — so a stub row is enough.
        var ownerId = Guid.NewGuid();
        if (!db.Users.Any(u => u.Username == "sparql-admin"))
        {
            db.Users.Add(new UserEntity
            {
                Id = ownerId,
                Username = "sparql-admin", DisplayName = "SPARQL Admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("dummy", workFactor: 4),
                IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        else
        {
            ownerId = db.Users.Single(u => u.Username == "sparql-admin").Id;
        }
        var ks = new KnowledgeSystemEntity
        {
            Id = Guid.NewGuid(),
            Name = $"ks-{tag}", Description = tag, OwnerId = ownerId,
            PublicId = $"pub-{tag}",
            BaseIri = $"http://example.com/{tag}#",
            GraphIri = $"http://example.com/graph/{tag}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks;
    }

    private static async Task SeedTurtleAsync(
        AuthTestWebApplicationFactory app, KnowledgeSystemEntity ks, string turtle)
    {
        // Use a scope to grab the live StoreWrapper so the test bytes land
        // in the same Oxigraph instance the executor reads.
        using var scope = app.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<StoreWrapper>();
        var ctx = KsContext.FromEntity(ks);
        store.LoadTurtle(
            Encoding.UTF8.GetBytes(turtle),
            new OntoNamedNode(ctx.TBoxGraph));
    }
}
