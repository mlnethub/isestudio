# ISEStudio Keycloak SSO 认证设计

**状态**: 设计获批,待实现
**日期**: 2026-08-28
**分支**: `main`
**范围**: `src/ISEStudio`(后端)+ `frontend/`(前端)+ `docker-compose.yml`(部署)
**参考**: `E:\gitee\goodcrew-pro\backend` + `E:\gitee\goodcrew-pro\frontend`
(Keycloak JWT Bearer + 手写 OIDC 的成熟实现)

给 ISEStudio 增加 Keycloak SSO 登录支持,与现有本地账号登录**并存**。

---

## 1. 背景

ISEStudio 当前认证:3 个 scheme —— `SessionCookie`(本地账号 BCrypt
+cookie 会话)、`ApiBearer`(knowledge API token)、`ExternalToken`(MCP
token)。前端 `frontend/src/lib/api.ts` 走 cookie,401 由
`setUnauthorizedHandler` 弹回登录页。

goodcrew-pro 参考实现已生产验证:

- 后端:JwtBearer + Keycloak authority,`MapInboundClaims=false`,
  azp 校验(public client 的 aud 恒为 `account`,azp 才是真凭据),
  FallbackPolicy 默认全站要登录
- 前端:**手写 OIDC**(无 keycloak-js SDK)——authorization code flow
  无 PKCE(http 环境 `crypto.subtle` 不可用)、sessionStorage 存
  token、state 事务校验(15min TTL + localStorage 自动重登冷却闸)、
  refresh_token 静默续期、401 清 token 重登、logout 带 id_token_hint
- 用户同步:UserSyncMiddleware 首次登录建行、每次刷新可变字段

## 2. 决策(用户已拍板)

| # | 决策点 | 结论 |
|---|--------|------|
| D1 | SSO 与本地登录关系 | **并存**——登录页加「SSO 登录」按钮,本地账号路径完全不动 |
| D2 | 会话形态 | **Bearer token 直调**——前端持 Keycloak access_token,后端加 JwtBearer scheme |
| D3 | 用户映射 | **自动同步**——首次 SSO 登录自动建本地用户行,admin 由 Keycloak realm role 决定 |
| D4 | 初始权限 | **默认无访问**——SSO 普通用户初始无任何 KS 权限,由 admin 授权(现有 KSGrant 机制) |
| D5 | 前端 OIDC 实现 | **手写**(照搬 goodcrew 模式),不引入 keycloak-js SDK |
| D6 | Keycloak 部署 | **compose 必选服务**(非 profile 可选),`docker compose up` 即得全套 |
| D7 | PKCE | 无 PKCE(goodcrew 同款取舍:自托管常为 http 环境,`crypto.subtle` 不可用;state 事务校验已挡住跨站伪造) |

## 3. 架构

```text
浏览器 ──(无有效 token)──▶ 登录页: [本地登录表单] / [SSO 登录按钮]
SSO 路径:
  前端 ──302──▶ Keycloak /realms/isestudio/protocol/openid-connect/auth
               (response_type=code, scope=openid profile email)
        ◀──302── /?code&state (redirect_uri 固定首页,深链靠 returnTo)
  前端 ──POST──▶ .../token (authorization_code → access/refresh/id token)
  前端 此后每个 API 请求: Authorization: Bearer <access_token>
后端:
  AddJwtBearer(Keycloak) 作为第 4 个 scheme
  OnTokenValidated: azp 校验 → SsoUserSyncService 同步(自动建行/刷新)
                    → 映射 realm role → Items["auth.user"] = UserEntity
  下游 KSRoleAuthorize / ResolveActor / 审计 / me —— 全部零改动复用
本地路径: SessionCookie / ApiBearer / ExternalToken —— 完全不动
```

**核心不变量:配置驱动激活。** `ISEStudio:Auth:Keycloak:Authority`
为空(默认)→ 不注册 JwtBearer、不装 PolicyScheme → 现有行为逐字节
不变,850 unit + 167 contract 回归全绿。SSO 是 opt-in。

## 4. 后端设计

### 4.1 配置节

```json
"ISEStudio": {
  "Auth": {
    "Keycloak": {
      "Authority": "",            // 空 = SSO 禁用
      "ClientId": "isestudio-frontend",
      "RequireHttpsMetadata": true,
      "AdminRole": "admin"         // realm role 名,含此 role → IsAdmin
    }
  }
}
```

### 4.2 scheme 共存(Program.cs)

- default scheme 改为 PolicyScheme + `ForwardDefaultSelector`:
  请求带 `Authorization: Bearer` 头 → `JwtBearer`;否则 →
  `SessionCookie`(它自己处理失败语义,现有 401 envelope 不变)
- `ApiBearer` / `ExternalToken` 保持显式 `[Authorize(Scheme=...)]`
  标注,不参与默认转发
- Keycloak 配置缺失时:不注册 JwtBearer、default 保持 SessionCookie

**selector 安全性(2026-08-28 全仓验证)**:所有带 `Bearer` 头的非
浏览器请求都不经过 default scheme——

| 客户端 | 认证路径 | 受 selector 影响 |
|--------|----------|------------------|
| 浏览器(cookie) | 裸 `[Authorize]` → default | 无 Bearer 头 → SessionCookie ✓ |
| 浏览器(SSO) | 裸 `[Authorize]` → default | 有 Bearer 头 → JwtBearer ✓ |
| Knowledge API token | `ApiBearerController` 显式 scheme | 否 |
| MCP / external API | `ExternalApiController` / `PublishedController` 显式 scheme | 否 |
| MCP 协议端点 | `McpTokenAuthenticationMiddleware`(独立 HTTP 中间件,不走 `[Authorize]`) | 否 |

裸 `[Authorize]` 的 15 个 controller 全部是浏览器场景;SSO 用户无
本地 session cookie、本地用户无 Bearer 头,两路互不串扰。

### 4.3 JwtBearer 配置(照搬 goodcrew AuthExtensions)

- `Authority = Keycloak:Authority`,`RequireHttpsMetadata` 可配
- `MapInboundClaims = false`(claim 保持 Keycloak 原名)
- `ValidateIssuer = true` / `ValidIssuer = Authority` /
  `ValidateAudience = false`(aud 无判定价值)/ `ValidateLifetime =
  true` / 30s skew / `NameClaimType = "preferred_username"`
- `OnTokenValidated`:
  1. **azp 门**:`azp` claim ≠ `ClientId` → `ctx.Fail`(无兼容
     client 名单——ISEStudio 单 client,goodcrew 的
     `ExtraClientIdsMetadata` 多端机制不移植)
  2. **角色映射**:`realm_access.roles` 数组逐个
     `identity.AddClaim(new Claim(ClaimTypes.Role, role))`
     —— `Policies.AdminOnly` 的 `RequireRole("Admin")` 依赖
     `User.IsInRole`,JWT 的嵌套 `realm_access` 不会自动成为
     role claim,必须手动摊平
  3. **用户同步**:IServiceScopeFactory 开 scope →
     `SsoUserSyncService.SyncAsync(principal)`
  4. `Items["auth.user"] = UserEntity`(与
     SessionAuthenticationHandler 同款挂点)

### 4.4 SsoUserSyncService(新文件,Authentication/)

```text
SyncAsync(principal, db, cfg):
  sub = FindFirst("sub")                     // 缺失 → InvalidOperation
  user = db.Users.FirstOrDefault(u => u.SubjectId == sub)
  if user is null:
      username = preferred_username ?? "sso_" + sub[..8]
      冲突(唯一索引) → username + "~" + sub[..8]   // sub 决定后缀,幂等
      user = new UserEntity {
          Username = username,
          DisplayName = name claim ?? username,
          PasswordHash = "",               // 空 hash = 不可本地密码登录
          IsAdmin = realmRoles.Contains(AdminRole),
          Active = true, CreatedAt = now
      }
      db.Users.Add(user)
  else:
      user.DisplayName = name claim(非空时)     // 刷新可变字段
      user.IsAdmin = realmRoles.Contains(AdminRole)
  if !user.Active → throw UnauthorizedAccessException("User inactive")
  db.SaveChanges()
  return user
```

- **并发**:`SubjectId` 唯一索引 + 捕获唯一约束冲突重查(与 goodcrew
  `DbUpdateException` 忽略同款,ISEStudio 用 advisory-lock 风格的重查)
- **AuthService.login 守卫**:`PasswordHash == ""` → 直接拒绝登录
  (BCrypt 验空 hash 本就会失败,显式守卫让语义可读、可测)

### 4.5 schema(EF migration)

- `UserEntity.SubjectId`: `string?`,唯一过滤索引(非空才建)
- 本地用户 SubjectId 恒 null,互不干扰

### 4.6 不改动的面

- `KSRoleAuthorize`:从 `Items["auth.user"]` 读 user →
  `GetEffectiveRoleAsync` → SSO 普通用户无 KSGrant → `None` → 403,
  与 D4 天然一致
- `InternalControllerBase.ResolveActor` / 审计 / `me` 端点 / MCP
  tokens / ExternalToken / ApiBearer —— 全部零改动

## 5. 前端设计

### 5.1 配置(三级,goodcrew 同款)

```
window.__ISE_AUTH__ (nginx 运行时注入,见 §7) > VITE_AUTH_* (构建期) > 未配置
未配置 = SSO 禁用:登录页不渲染 SSO 按钮,api.ts 原样走 cookie
```

### 5.2 新文件 `src/lib/sso/authModel.ts`(纯函数,可测)

- `AUTHORITY` / `CLIENT_ID` 三级解析;OIDC 端点 URL 拼装
- `buildAuthUrl(redirectUri, state)`:`response_type=code`、
  `scope=openid profile email`、无 PKCE(D7)
- `parseCallback(search)`:Keycloak `error` 参数 → **抛错**(静默当作
  未登录会跳回死循环);无 code → null
- `buildLogoutUrl(redirectUri, idToken)`:`id_token_hint` +
  `post_logout_redirect_uri`
- `needsRefresh(expiresAtMs, nowMs)`:60s skew
- `randomState()`:`crypto.getRandomValues`(http 下也可用)

### 5.3 新文件 `src/lib/sso/auth.ts`(状态机)

- state 事务:sessionStorage(`sso_login_{state}`)存
  `{returnTo, createdAt, exchangeStarted}`,15min TTL;回调对不上
  → 四种原因分开报(no_state / unknown_transaction / expired /
  already_exchanged,goodcrew 现场诊断的教训)
- 自动重登冷却闸:localStorage(`sso_auto_relogin_at`,60s),写后读回
  确认——没有闸的自动跳转是死循环
- `login()`:生成 state → 存事务 → `location.assign(buildAuthUrl)`
- `ensureAuthenticated()`:处理回调(换票 → 清事务 →
  `history.replaceState` 回 returnTo)/ 有 token 直接过 / 否则 login
  —— 返回永不 settle 的 halt Promise 把后续挂住
- `getToken()`:access 未过期直接用;过期用 refresh_token 静默续期
  (`exchange({grant_type:"refresh_token"})`);refresh 失败 → 清空 →
  重登
- `logout()`:清 sessionStorage → 跳 Keycloak logout
  (id_token_hint)
- **去 goodcrew 的 X-Organization 全部逻辑**(ISEStudio 无组织概念)
- **去 keycloakReachable / 组织清单死循环防护**(无组织清单可拉)

### 5.4 改造现有文件

| 文件 | 改动 |
|------|------|
| `lib/api.ts` | `request()` 请求头注入 `Authorization: Bearer`(SSO 启用且有 token 时);401 时若 SSO 模式 → 清 token(现有 `onUnauthorized` handler 链保留,把「回登录页」语义接上 SSO 的 login) |
| `lib/auth.tsx` | `AuthProvider` 挂载时:SSO 启用 → 先 `ensureAuthenticated()` 再 `api.me()`;`logout()` 分叉:SSO 用户走 `sso.logout()`,本地用户走现有 `/api/auth/logout` |
| `pages/LoginPage.tsx` | SSO 启用时渲染「使用 SSO 登录」按钮 → `sso.login()`;本地表单不动 |
| `main.tsx` | 无需改(AuthProvider 内做 ensureAuthenticated) |
| `lib/i18n.tsx`(zh/en) | 新键:`login.ssoButton` 等 |

### 5.5 401 语义对齐

后端 JwtBearer 失败默认返回 401(无 body)+ `WWW-Authenticate`。
前端现有 `onUnauthorized` 链不依赖 body,兼容。403 语义不变
(KSRoleAuthorize 的 envelope 原样)。

## 6. 测试

### 6.1 后端

| 测试 | 覆盖 |
|------|------|
| `SsoUserSyncServiceTests`(单测,~8 例) | 首次建行 / 已有刷新 DisplayName+IsAdmin / username 冲突后缀幂等 / Active=false 拒绝 / AdminRole 映射 / sub 缺失抛错 / 空 hash 不可登录 |
| `SsoJwtBearerTests`(集成,~6 例) | 用 `TestJwtIssuer`(RS256 自签私钥签发 token + 假 discovery/jwks 端点)走 WebApplicationFactory:合法 token 建行后 `/api/auth/me` 返 UserOut / azp 不符 401 / 过期 401 / realm role admin → AdminOnly 端点 200 / 无配置不激活(默认工厂 167 contract 基线即证明) |
| `AuthServiceTests`(补 1 例) | 空 PasswordHash 登录被拒 |

### 6.2 前端(vitest)

| 测试 | 覆盖 |
|------|------|
| `authModel.test.ts`(~10 例,照搬 goodcrew 现有 test) | 三级配置解析 / buildAuthUrl 参数 / parseCallback(error 抛错、无 code 返 null、有 code) / needsRefresh 边界 / buildLogoutUrl / randomState 长度 |

### 6.3 回归门

- 无 Keycloak 配置:850 unit + 167 contract 全绿(现有基线不动)
- `docker compose config --quiet` exit 0
- `dotnet build` 0 warn

## 7. 部署(必选,D6)

### 7.1 compose 服务

```yaml
keycloak:
  image: quay.io/keycloak/keycloak:26
  command: start --import-realm  # 挂载 deploy/keycloak/isestudio-realm.json
  environment: KC_DB=postgres, KEYCLOAK_ADMIN=admin, KEYCLOAK_ADMIN_PASSWORD=...
  depends_on: keycloak-postgres (postgres:16,独立实例)
backend: 环境变量 ISEStudio__Auth__Keycloak__Authority=http://keycloak:8080/realms/isestudio
        + ClientId / AdminRole / RequireHttpsMetadata=false(容器内 http)
frontend: ISE_AUTH_AUTHORITY / ISE_AUTH_CLIENT_ID 环境变量
```

### 7.2 realm 初始化文件

- 新 `deploy/keycloak/isestudio-realm.json`:realm `isestudio`、
  public client `isestudio-frontend`(redirect 白名单:首页)、realm
  role `admin`、演示用户(密码写在 compose `.env` 注释,生产改)
- Keycloak 启动 `--import-realm` 自动导入

### 7.3 前端运行时注入(一镜像多环境)

- frontend 容器 entrypoint:envsubst 把 `ISE_AUTH_AUTHORITY` /
  `ISE_AUTH_CLIENT_ID` 写进 `index.html` 的
  `window.__ISE_AUTH__`(goodcrew AuthConfigInjection 的自托管等价物
  ——后端不托管前端静态文件,注入点从后端移到 nginx 容器)
- 无 env 变量 → 不注入 → 前端回落 VITE_ → 再回落禁用

### 7.4 配置文档

- `src/.env.example` 加 3 个 Keycloak 键
- README 部署章节:realm 导入、client 配置、admin role 授予方法

## 8. 文件清单(预估)

| 域 | 文件 | 性质 |
|----|------|------|
| 后端 | `Program.cs` | 改:PolicyScheme + 条件注册 JwtBearer |
| 后端 | `Authentication/SsoUserSyncService.cs` | 新 |
| 后端 | `Authentication/SsoOptions.cs`(或并入配置读取处) | 新 |
| 后端 | `Infrastructure/Persistence/Entities/AuthEntities.cs` | 改:UserEntity.SubjectId |
| 后端 | `Infrastructure/Persistence/Migrations/*_SsoSubjectId.cs` | 新 migration |
| 后端 | `Authentication/AuthService.cs` | 改:空 hash 守卫 |
| 后端 | `ISEStudio.Tests/.../SsoUserSyncServiceTests.cs` | 新 |
| 后端 | `ISEStudio.Tests/.../SsoJwtBearerTests.cs` + `TestJwtIssuer.cs` | 新 |
| 前端 | `src/lib/sso/authModel.ts` / `auth.ts` | 新 |
| 前端 | `src/lib/sso/authModel.test.ts` | 新 |
| 前端 | `lib/api.ts` / `lib/auth.tsx` / `pages/LoginPage.tsx` / `lib/i18n.tsx` | 改 |
| 部署 | `docker-compose.yml` | 改:keycloak + keycloak-postgres + env |
| 部署 | `deploy/keycloak/isestudio-realm.json` | 新 |
| 部署 | `frontend/Dockerfile`(entrypoint 注入脚本) | 改 |
| 部署 | `src/.env.example` / README | 改 |

## 9. 明确不做(out of scope)

- goodcrew 的 FallbackPolicy 默认全站登录(ISEStudio 逐 endpoint
  标注 + RBAC 矩阵测试锁定,不做全局翻转)
- WebSocket query token 旁路(ISEStudio 无 WS 端点)
- 多 client 兼容名单(ExtraClientIdsMetadata)
- 组织/租户概念(Keycloak organization scope 不移植)
- MCP tokens / Knowledge API tokens 的 SSO 化(它们继续走
  ExternalToken / ApiBearer,正交不动)

## 10. Decision Log

- 2026-08-28: 用户拍板 D1-D7(并存 / bearer 直调 / 自动同步 /
  默认无访问 / 手写 OIDC / compose 必选 / 无 PKCE)。设计呈现后获批。
