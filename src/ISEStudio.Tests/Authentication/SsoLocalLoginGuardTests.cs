using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Tests.Authentication;

/// <summary>
/// SSO 用户的空 PasswordHash 不能被本地密码登录——且登录尝试走完整
/// BCrypt 计时(不因 hash 为空而短路)。spec §4.4 / 计划 Task 3。
/// </summary>
public class SsoLocalLoginGuardTests
{
    [Fact]
    public async Task SsoUserCannotLoginWithPassword()
    {
        await using var factory = new AuthTestWebApplicationFactory();
        var db = factory.CreateDbContext();
        db.Users.Add(new UserEntity
        {
            Username = "sso_user",
            DisplayName = "SSO User",
            PasswordHash = string.Empty,   // SSO 建行语义
            IsAdmin = false,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "sso_user",
            password = "anything",
        });

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}