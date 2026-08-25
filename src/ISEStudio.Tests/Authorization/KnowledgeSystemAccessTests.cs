using Microsoft.EntityFrameworkCore;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Tests.Persistence;
using Xunit;

namespace ISEStudio.Tests.Authorization;

/// <summary>
/// Tests the per-knowledge-system role matrix:
/// admin / KS owner ⇒ effective role <c>Owner</c>; grants give <c>Viewer</c>/<c>Editor</c>;
/// otherwise <c>None</c>. The hierarchy <c>Viewer &lt; Editor &lt; Owner</c> is enforced by
/// <see cref="KnowledgeSystemAccessService.HasAtLeastAsync"/>.
/// </summary>
public sealed class KnowledgeSystemAccessTests
{
    [Fact]
    public async Task Admin_user_is_owner_everywhere()
    {
        using var db = DbContextFactory.CreateSqlite();
        var (admin, ks, _) = await SeedAsync(db, newUser: true);
        var sut = new KnowledgeSystemAccessService();

        var role = await sut.GetEffectiveRoleAsync(admin, ks, db, CancellationToken.None);

        Assert.Equal(KSRole.Owner, role);
    }

    [Fact]
    public async Task Knowledge_system_owner_is_owner_of_their_own_ks()
    {
        using var db = DbContextFactory.CreateSqlite();
        var (_, ks, owner) = await SeedAsync(db, newUser: true);
        var sut = new KnowledgeSystemAccessService();

        var role = await sut.GetEffectiveRoleAsync(owner, ks, db, CancellationToken.None);

        Assert.Equal(KSRole.Owner, role);
    }

    [Fact]
    public async Task Viewer_grant_gives_viewer_role()
    {
        using var db = DbContextFactory.CreateSqlite();
        var (_, ks, _) = await SeedAsync(db, newUser: false);
        var grantee = await AddUserAsync(db, "grantee");
        db.KSGrants.Add(new KSGrantEntity
        {
            LegacyId = TestLegacyIds.Next("ksgrant"),
            KnowledgeSystemId = ks.Id,
            UserId = grantee.Id,
            Role = "viewer",
        });
        db.SaveChanges();

        var sut = new KnowledgeSystemAccessService();
        var role = await sut.GetEffectiveRoleAsync(grantee, ks, db, CancellationToken.None);

        Assert.Equal(KSRole.Viewer, role);
    }

    [Fact]
    public async Task Editor_grant_gives_editor_role()
    {
        using var db = DbContextFactory.CreateSqlite();
        var (_, ks, _) = await SeedAsync(db, newUser: false);
        var grantee = await AddUserAsync(db, "editor-user");
        db.KSGrants.Add(new KSGrantEntity
        {
            LegacyId = TestLegacyIds.Next("ksgrant"),
            KnowledgeSystemId = ks.Id,
            UserId = grantee.Id,
            Role = "editor",
        });
        db.SaveChanges();

        var sut = new KnowledgeSystemAccessService();
        var role = await sut.GetEffectiveRoleAsync(grantee, ks, db, CancellationToken.None);

        Assert.Equal(KSRole.Editor, role);
    }

    [Fact]
    public async Task No_grant_returns_none_role()
    {
        using var db = DbContextFactory.CreateSqlite();
        var (_, ks, _) = await SeedAsync(db, newUser: false);
        var stranger = await AddUserAsync(db, "stranger");

        var sut = new KnowledgeSystemAccessService();
        var role = await sut.GetEffectiveRoleAsync(stranger, ks, db, CancellationToken.None);

        Assert.Equal(KSRole.None, role);
    }

    [Theory]
    [InlineData(KSRole.Viewer, true)]
    [InlineData(KSRole.Editor, false)]
    [InlineData(KSRole.Owner, false)]
    public async Task Viewer_role_only_satisfies_viewer_requirement(KSRole required, bool expected)
    {
        using var db = DbContextFactory.CreateSqlite();
        var (_, ks, _) = await SeedAsync(db, newUser: false);
        var grantee = await AddUserAsync(db, "viewer-user");
        db.KSGrants.Add(new KSGrantEntity
        {
            LegacyId = TestLegacyIds.Next("ksgrant"),
            KnowledgeSystemId = ks.Id,
            UserId = grantee.Id,
            Role = "viewer",
        });
        db.SaveChanges();

        var sut = new KnowledgeSystemAccessService();
        var actual = await sut.HasAtLeastAsync(grantee, ks, required, db, CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(KSRole.Viewer, true)]
    [InlineData(KSRole.Editor, true)]
    [InlineData(KSRole.Owner, false)]
    public async Task Editor_satisfies_viewer_and_editor_but_not_owner(KSRole required, bool expected)
    {
        using var db = DbContextFactory.CreateSqlite();
        var (_, ks, _) = await SeedAsync(db, newUser: false);
        var grantee = await AddUserAsync(db, "editor-user");
        db.KSGrants.Add(new KSGrantEntity
        {
            LegacyId = TestLegacyIds.Next("ksgrant"),
            KnowledgeSystemId = ks.Id,
            UserId = grantee.Id,
            Role = "editor",
        });
        db.SaveChanges();

        var sut = new KnowledgeSystemAccessService();
        var actual = await sut.HasAtLeastAsync(grantee, ks, required, db, CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(KSRole.Viewer, true)]
    [InlineData(KSRole.Editor, true)]
    [InlineData(KSRole.Owner, true)]
    public async Task Owner_satisfies_all_requirements(KSRole required, bool expected)
    {
        using var db = DbContextFactory.CreateSqlite();
        var (_, ks, owner) = await SeedAsync(db, newUser: true);

        var sut = new KnowledgeSystemAccessService();
        var actual = await sut.HasAtLeastAsync(owner, ks, required, db, CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Admin_satisfies_owner_requirement_on_foreign_ks()
    {
        using var db = DbContextFactory.CreateSqlite();
        var (admin, ks, _) = await SeedAsync(db, newUser: false);

        var sut = new KnowledgeSystemAccessService();
        var actual = await sut.HasAtLeastAsync(admin, ks, KSRole.Owner, db, CancellationToken.None);

        Assert.True(actual);
    }

    [Fact]
    public async Task None_role_fails_every_requirement()
    {
        using var db = DbContextFactory.CreateSqlite();
        var (_, ks, _) = await SeedAsync(db, newUser: false);
        var stranger = await AddUserAsync(db, "stranger");

        var sut = new KnowledgeSystemAccessService();

        Assert.False(await sut.HasAtLeastAsync(stranger, ks, KSRole.Viewer, db, CancellationToken.None));
        Assert.False(await sut.HasAtLeastAsync(stranger, ks, KSRole.Editor, db, CancellationToken.None));
        Assert.False(await sut.HasAtLeastAsync(stranger, ks, KSRole.Owner, db, CancellationToken.None));
    }

    private static async Task<(UserEntity admin, KnowledgeSystemEntity ks, UserEntity owner)> SeedAsync(
        ISEStudio.Infrastructure.Persistence.ISEStudioDbContext db, bool newUser)
    {
        var now = DateTimeOffset.UtcNow;
        var admin = new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = newUser ? "admin" : $"admin-{Guid.NewGuid():N}",
            DisplayName = "Admin",
            PasswordHash = "x",
            IsAdmin = true,
            Active = true,
            CreatedAt = now,
        };
        var owner = new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = "owner",
            DisplayName = "Owner",
            PasswordHash = "x",
            IsAdmin = false,
            Active = true,
            CreatedAt = now,
        };
        db.Users.Add(admin);
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "Test KS",
            Description = "Test KS",
            OwnerId = owner.Id,
            GraphIri = $"http://goodcrew.local/ks/{Guid.NewGuid():N}",
            BaseIri = $"http://goodcrew.local/ks/{Guid.NewGuid():N}#",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.KnowledgeSystems.Add(ks);
        await db.SaveChangesAsync();

        // SQL Server/SQLite friendly: detach references before returning.
        db.ChangeTracker.Clear();
        return (admin, ks, owner);
    }

    private static async Task<UserEntity> AddUserAsync(
        ISEStudio.Infrastructure.Persistence.ISEStudioDbContext db, string name)
    {
        var user = new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = $"{name}-{Guid.NewGuid():N}",
            DisplayName = name,
            PasswordHash = "x",
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
