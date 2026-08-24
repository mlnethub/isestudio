using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OnToPilot.Authentication;
using OnToPilot.Authorization;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Persistence;
using Xunit;

namespace OnToPilot.Tests.Authorization;

/// <summary>
/// Unit tests for the <see cref="KSRoleAuthorizeAttribute"/> action filter,
/// exercising the resolution branches directly via
/// <see cref="AuthorizationFilterContext"/> (no HTTP server):
/// <list type="number">
///   <item>missing <c>auth.user</c> ⇒ 401 <c>Not authenticated</c></item>
///   <item><c>ExternalToken</c>/<c>ApiBearer</c> principal + <c>AllowExternalToken</c> ⇒ bypass</item>
///   <item>missing route argument ⇒ 400 <c>Missing knowledge system identifier</c></item>
///   <item>unknown KS ⇒ 404 <c>Knowledge system not found</c></item>
///   <item>no grant (role <c>None</c>) ⇒ 403 <c>You don't have access to this knowledge system</c></item>
///   <item>role below <c>Minimum</c> ⇒ 403 <c>Insufficient permissions</c></item>
///   <item>admin on <c>Owner</c> minimum ⇒ passes</item>
///   <item>route value as <see cref="Guid"/> instance ⇒ resolved by Id</item>
///   <item>route value as publicId string ⇒ resolved by PublicId</item>
/// </list>
/// The db is an in-memory SQLite context via the same
/// <see cref="DbContextFactory.CreateSqlite"/> fixture used by
/// <see cref="KnowledgeSystemAccessTests"/>.
/// </summary>
public sealed class KSRoleAuthorizeFilterTests
{
    [Fact]
    public async Task Missing_auth_user_yields_401_not_authenticated()
    {
        await using var harness = new Harness();
        var attribute = new KSRoleAuthorizeAttribute(KSRole.Viewer);
        var context = harness.BuildContext(attribute, user: null, routeId: null, authScheme: null);

        await attribute.OnAuthorizationAsync(context);

        AssertResult(context, StatusCodes.Status401Unauthorized, "Not authenticated");
    }

    [Theory]
    [InlineData(ExternalTokenAuthenticationHandler.SchemeName)]
    [InlineData(ApiBearerAuthenticationHandler.SchemeName)]
    public async Task External_token_principal_bypasses_role_check(string scheme)
    {
        await using var harness = new Harness();
        var attribute = new KSRoleAuthorizeAttribute(KSRole.Owner) { AllowExternalToken = true };
        var user = await SeedUserAsync(harness.Db, "token-holder");
        // No route id at all — the bypass must return before KS resolution.
        var context = harness.BuildContext(attribute, user, routeId: null, authScheme: scheme);

        await attribute.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Missing_route_argument_yields_400()
    {
        await using var harness = new Harness();
        var attribute = new KSRoleAuthorizeAttribute(KSRole.Viewer);
        var user = await SeedUserAsync(harness.Db, "viewer");
        var context = harness.BuildContext(attribute, user, routeId: null, authScheme: null);

        await attribute.OnAuthorizationAsync(context);

        AssertResult(context, StatusCodes.Status400BadRequest, "Missing knowledge system identifier");
    }

    [Fact]
    public async Task Unknown_knowledge_system_yields_404_not_found()
    {
        await using var harness = new Harness();
        var attribute = new KSRoleAuthorizeAttribute(KSRole.Viewer);
        var user = await SeedUserAsync(harness.Db, "editor");
        // String route value, as MVC produces for a {id:guid} template.
        var context = harness.BuildContext(attribute, user, Guid.NewGuid().ToString(), authScheme: null);

        await attribute.OnAuthorizationAsync(context);

        AssertResult(context, StatusCodes.Status404NotFound, "Knowledge system not found");
    }

    [Fact]
    public async Task User_without_grant_yields_403_no_access()
    {
        await using var harness = new Harness();
        var attribute = new KSRoleAuthorizeAttribute(KSRole.Viewer);
        var owner = await SeedUserAsync(harness.Db, "owner");
        var ks = await SeedKnowledgeSystemAsync(harness.Db, owner);
        var stranger = await SeedUserAsync(harness.Db, "stranger");
        var context = harness.BuildContext(attribute, stranger, ks.Id.ToString(), authScheme: null);

        await attribute.OnAuthorizationAsync(context);

        AssertResult(context, StatusCodes.Status403Forbidden, "You don't have access to this knowledge system");
    }

    [Fact]
    public async Task Viewer_on_editor_minimum_yields_403_insufficient_permissions()
    {
        await using var harness = new Harness();
        var attribute = new KSRoleAuthorizeAttribute(KSRole.Editor);
        var owner = await SeedUserAsync(harness.Db, "owner");
        var ks = await SeedKnowledgeSystemAsync(harness.Db, owner);
        var viewer = await SeedUserAsync(harness.Db, "viewer");
        await SeedGrantAsync(harness.Db, ks.Id, viewer.Id, "viewer");
        var context = harness.BuildContext(attribute, viewer, ks.Id.ToString(), authScheme: null);

        await attribute.OnAuthorizationAsync(context);

        AssertResult(context, StatusCodes.Status403Forbidden, "Insufficient permissions");
    }

    [Fact]
    public async Task Admin_passes_owner_minimum()
    {
        await using var harness = new Harness();
        var attribute = new KSRoleAuthorizeAttribute(KSRole.Owner);
        var owner = await SeedUserAsync(harness.Db, "owner");
        var ks = await SeedKnowledgeSystemAsync(harness.Db, owner);
        var admin = await SeedUserAsync(harness.Db, "admin", isAdmin: true);
        var context = harness.BuildContext(attribute, admin, ks.Id.ToString(), authScheme: null);

        await attribute.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Guid_route_value_resolves_by_id()
    {
        await using var harness = new Harness();
        var attribute = new KSRoleAuthorizeAttribute(KSRole.Viewer);
        var owner = await SeedUserAsync(harness.Db, "owner");
        var ks = await SeedKnowledgeSystemAsync(harness.Db, owner);
        var viewer = await SeedUserAsync(harness.Db, "viewer");
        await SeedGrantAsync(harness.Db, ks.Id, viewer.Id, "viewer");
        var context = harness.BuildContext(attribute, viewer, ks.Id, authScheme: null);

        await attribute.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Public_id_route_value_resolves_by_public_id()
    {
        await using var harness = new Harness();
        var attribute = new KSRoleAuthorizeAttribute(KSRole.Viewer) { RouteArgument = "publicId" };
        var owner = await SeedUserAsync(harness.Db, "owner");
        var ks = await SeedKnowledgeSystemAsync(harness.Db, owner);
        var viewer = await SeedUserAsync(harness.Db, "viewer");
        await SeedGrantAsync(harness.Db, ks.Id, viewer.Id, "viewer");
        var context = harness.BuildContext(attribute, viewer, ks.PublicId, authScheme: null);

        await attribute.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    // ------------------------------------------------------------------
    // Assertions / fixtures
    // ------------------------------------------------------------------

    private static void AssertResult(AuthorizationFilterContext context, int statusCode, string detail)
    {
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(statusCode, result.StatusCode);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(detail, doc.RootElement.GetProperty("detail").GetString());
    }

    private static async Task<UserEntity> SeedUserAsync(OnToPilotDbContext db, string name, bool isAdmin = false)
    {
        var user = new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = $"{name}-{Guid.NewGuid():N}",
            DisplayName = name,
            PasswordHash = "x",
            IsAdmin = isAdmin,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<KnowledgeSystemEntity> SeedKnowledgeSystemAsync(
        OnToPilotDbContext db, UserEntity owner)
    {
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Test KS",
            Description = "Test KS",
            OwnerId = owner.Id,
            GraphIri = $"http://goodcrew.local/ks/{Guid.NewGuid():N}",
            BaseIri = $"http://goodcrew.local/ks/{Guid.NewGuid():N}#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();
        return ks;
    }

    private static async Task SeedGrantAsync(OnToPilotDbContext db, Guid ksId, Guid userId, string role)
    {
        db.KSGrants.Add(new KSGrantEntity
        {
            LegacyId = TestLegacyIds.Next("ksgrant"),
            KnowledgeSystemId = ksId,
            UserId = userId,
            Role = role,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Owns an in-memory SQLite <see cref="OnToPilotDbContext"/> plus a
    /// <see cref="ServiceProvider"/> exposing it (and the stateless
    /// <see cref="KnowledgeSystemAccessService"/>) through
    /// <c>HttpContext.RequestServices</c>, which is how the filter resolves
    /// its dependencies in production.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        public Harness()
        {
            Db = DbContextFactory.CreateSqlite();
            _services = new ServiceCollection()
                .AddSingleton<OnToPilotDbContext>(Db)
                .AddSingleton<KnowledgeSystemAccessService>()
                .BuildServiceProvider();
        }

        public OnToPilotDbContext Db { get; }

        public AuthorizationFilterContext BuildContext(
            KSRoleAuthorizeAttribute attribute,
            UserEntity? user,
            object? routeId,
            string? authScheme)
        {
            var http = new DefaultHttpContext { RequestServices = _services };
            if (user is not null)
            {
                http.Items[SessionAuthenticationHandler.UserItemKey] = user;
            }
            if (authScheme is not null)
            {
                http.User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: authScheme));
            }
            var context = new AuthorizationFilterContext(
                new ActionContext(http, new RouteData(), new ActionDescriptor()),
                new List<IFilterMetadata>());
            if (routeId is not null)
            {
                context.RouteData.Values[attribute.RouteArgument] = routeId;
            }
            return context;
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }
}
