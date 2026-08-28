using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Authentication;

/// <summary>
/// Keycloak 认证配置。Authority 为空 = SSO 特性整体禁用(不注册
/// JwtBearer、default scheme 保持 SessionCookie)。
/// </summary>
public sealed class SsoOptions
{
    public const string SectionName = "ISEStudio:Auth:Keycloak";

    public string Authority { get; set; } = string.Empty;

    /// <summary>Keycloak public client 的 clientId;azp 断言用。</summary>
    public string ClientId { get; set; } = "isestudio-frontend";

    /// <summary>默认必须 https;容器内 http 部署显式置 false。</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// 可选:JwtBearer 拉 discovery/JWKS 的地址。默认从 Authority 派生。
    /// 容器部署双 URL:Authority 是浏览器可见地址(iss 校验基准),但后端
    /// 容器内访问不了它——此键指向容器内 Keycloak 地址(见 deploy 计划
    /// Task 1 的 backend 环境接线)。
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>realm role 名;含此 role → 本地用户 IsAdmin=true。</summary>
    public string AdminRole { get; set; } = "admin";

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Authority);
}

/// <summary>
/// Keycloak JWT claim 的纯函数映射。<c>realm_access</c> 在 JWT 里是
/// <c>{"roles":[...]}</c> 嵌套 JSON,不会自动变成 role claim——
/// Policies.AdminOnly 的 RequireRole 依赖摊平后的 ClaimTypes.Role。
/// </summary>
public static class SsoClaimMapping
{
    public static IEnumerable<string> RealmRoles(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst("realm_access")?.Value;
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException) { yield break; }
        if (!doc.RootElement.TryGetProperty("roles", out var roles)
            || roles.ValueKind != JsonValueKind.Array) yield break;
        foreach (var element in roles.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String
                && element.GetString() is { Length: > 0 } role)
                yield return role;
        }
    }
}

/// <summary>
/// SSO 登录的用户同步:sub 查行 → 无则建行(空 PasswordHash = 不可本地
/// 密码登录)→ 有则刷新可变字段(DisplayName / IsAdmin)→ Active=false
/// 拒绝。写回 <c>Items["auth.user"]</c> 由调用方(JwtBearer
/// OnTokenValidated)负责。
/// </summary>
public sealed class SsoUserSyncService
{
    private readonly ISEStudioDbContext _db;
    private readonly IOptions<SsoOptions> _options;
    private readonly TimeProvider _clock;

    public SsoUserSyncService(
        ISEStudioDbContext db,
        IOptions<SsoOptions> options,
        TimeProvider clock)
    {
        _db = db;
        _options = options;
        _clock = clock;
    }

    public async Task<UserEntity> SyncAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var sub = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(sub))
        {
            throw new InvalidOperationException("SSO token missing sub claim");
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.SubjectId == sub, ct)
            .ConfigureAwait(false);

        var roles = SsoClaimMapping.RealmRoles(principal)
            .ToHashSet(StringComparer.Ordinal);
        var isAdmin = roles.Contains(_options.Value.AdminRole);
        var displayName = principal.FindFirst("name")?.Value;
        var preferredUsername = principal.FindFirst("preferred_username")?.Value;

        if (user is null)
        {
            user = await CreateAsync(sub, preferredUsername, displayName, isAdmin, ct)
                .ConfigureAwait(false);
            return user;
        }

        if (!user.Active)
        {
            throw new UnauthorizedAccessException("User inactive");
        }

        // 每次登录刷新可变字段(Keycloak 侧改了 role / 名字下次登录生效)。
        user.IsAdmin = isAdmin;
        if (!string.IsNullOrWhiteSpace(displayName)) user.DisplayName = displayName;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    private async Task<UserEntity> CreateAsync(
        string sub, string? preferredUsername, string? displayName,
        bool isAdmin, CancellationToken ct)
    {
        var baseName = !string.IsNullOrWhiteSpace(preferredUsername)
            ? preferredUsername.Trim()
            : $"sso_{sub[..Math.Min(8, sub.Length)]}";
        var entity = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = await UniqueUsernameAsync(baseName, sub, ct).ConfigureAwait(false),
            DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : baseName,
            PasswordHash = string.Empty,
            IsAdmin = isAdmin,
            Active = true,
            SubjectId = sub,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Users.Add(entity);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return entity;
        }
        catch (DbUpdateException)
        {
            // 并发建行竞态:另一请求已建同 sub 行 → 重查返回既有行。
            var existing = await _db.Users
                .FirstOrDefaultAsync(u => u.SubjectId == sub, ct)
                .ConfigureAwait(false);
            if (existing is not null) return existing;
            throw;
        }
    }

    private async Task<string> UniqueUsernameAsync(string baseName, string sub, CancellationToken ct)
    {
        var taken = await _db.Users
            .AnyAsync(u => u.Username == baseName, ct)
            .ConfigureAwait(false);
        // sub 决定后缀 → 同一 Keycloak 账号重复登录幂等,不与本地用户撞名。
        return taken ? $"{baseName}~{sub[..Math.Min(8, sub.Length)]}" : baseName;
    }
}