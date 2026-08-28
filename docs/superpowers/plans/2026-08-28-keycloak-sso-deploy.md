# Keycloak SSO 部署实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 Keycloak 作为 compose 必选服务(非 profile)接入 ISEStudio 自托管栈,`docker compose up -d --build` 即得 SSO 全套;一个 frontend 镜像通过 entrypoint 运行时注入适配任意 realm。

**Architecture:** 双 URL 方案(解决"浏览器/后端容器看到的 Keycloak 地址不同"的经典多主机问题):浏览器经宿主端口 `${KEYCLOAK_PORT:-8081}` 直连 Keycloak;后端 `Authority` 配浏览器可见 URL(仅作 token `iss` 校验基准),`MetadataAddress` 配容器内 `http://keycloak:8080/...`(后端从这里拉 discovery + JWKS,签名验证)。Keycloak `KC_HOSTNAME_STRICT=false` 让 issuer 按请求 Host 动态生成——iss 与浏览器 URL 一致,校验通过。前端镜像不烧死 realm:entrypoint 用 sed 把 `ISE_AUTH_AUTHORITY` / `ISE_AUTH_CLIENT_ID` 注入 `index.html` 的 `window.__ISE_AUTH__`(goodcrew AuthConfigInjection 的自托管等价物,注入点从后端移到 nginx 容器)。

**Tech Stack:** docker compose / quay.io/keycloak/keycloak:26 / postgres:16-alpine / nginx:alpine + sed(免装 gettext)/ EF Core env-var 绑定。

**Spec:** [2026-08-28-keycloak-sso-design.md](../specs/2026-08-28-keycloak-sso-design.md)(§7 部署、§2 D6 必选)
**Backend 配套:** 本计划的 `MetadataAddress` 依赖后端计划 Task 2/4 的 `SsoOptions.MetadataAddress` 字段——**先执行后端计划**再验证本计划。

## Global Constraints

- `docker compose config --quiet` exit 0(spec §6.3 门)
- Keycloak 服务**必选**(无 profile),与 postgres/minio 并列
- 新 env 键全部进三个 `.env.example`(root / src / frontend),README 部署章节同步
- 生产切 https 时:删 realm `sslRequired: "none"`、`RequireHttpsMetadata` 改 true、redirect 白名单改域名——计划内注释标注
- 提交风格:`feat(sso): ...` + 尾随 `Co-Authored-By: Claude <noreply@anthropic.com>`
- 演示凭据只出现在 realm JSON / .env.example 的注释里,并标注"生产必改"

---

### Task 1: compose — keycloak + keycloak-postgres + env 接线

**Files:**
- Modify: `docker-compose.yml`
- Modify: `.env.example`(root)

**Interfaces:**
- Consumes: 后端 `SsoOptions`(Authority / MetadataAddress / ClientId / RequireHttpsMetadata / AdminRole,backend 计划);前端 entrypoint env 键 `ISE_AUTH_AUTHORITY` / `ISE_AUTH_CLIENT_ID`(本计划 Task 3)。
- Produces: `keycloak` / `keycloak-postgres` 服务;root env 键 `KEYCLOAK_DB_PASSWORD` / `KEYCLOAK_ADMIN_USER` / `KEYCLOAK_ADMIN_PASSWORD` / `KEYCLOAK_PORT` / `ISE_AUTH_AUTHORITY` / `ISE_AUTH_CLIENT_ID` / `ISE_AUTH_ADMIN_ROLE`。

- [ ] **Step 1: 加两个服务**

在 [docker-compose.yml:33](docker-compose.yml#L33) 的 `minio` 服务之后(minio 块结束、`# ---- migrate` 注释之前)插入:

```yaml
  # ---- Keycloak (SSO, D6 必选服务) ----
  # 浏览器经宿主 ${KEYCLOAK_PORT:-8081} 直连 Keycloak;后端容器内经
  # keycloak:8080 拉 discovery/JWKS(MetadataAddress),iss 校验用浏览器
  # 可见的 Authority(见 backend 服务的 ISEStudio__Auth__Keycloak__* 注释)。
  # --hostname-strict=false:自托管 http 部署下 Keycloak 按请求 Host 动态
  # 生成 issuer,与浏览器 URL 一致;生产切 https + 固定域名时应收紧。
  keycloak-postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: keycloak
      POSTGRES_USER: keycloak
      POSTGRES_PASSWORD: ${KEYCLOAK_DB_PASSWORD:?Set KEYCLOAK_DB_PASSWORD in the root .env file}
    volumes:
      - isestudio-keycloak-postgres:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U keycloak -d keycloak"]
      interval: 5s
      timeout: 5s
      retries: 20
    restart: unless-stopped

  keycloak:
    image: quay.io/keycloak/keycloak:26
    # --import-realm:首次启动导入 /opt/keycloak/data/import/ 下的 realm JSON
    # (已导入后 re-run 不重复导入)。--health-enabled 打开 9000 管理端口
    # 供健康检查。
    command: start --import-realm --health-enabled=true
    environment:
      KC_DB: postgres
      KC_DB_URL_HOST: keycloak-postgres
      KC_DB_URL_DATABASE: keycloak
      KC_DB_USERNAME: keycloak
      KC_DB_PASSWORD: ${KEYCLOAK_DB_PASSWORD}
      KC_BOOTSTRAP_ADMIN_USERNAME: ${KEYCLOAK_ADMIN_USER:-admin}
      KC_BOOTSTRAP_ADMIN_PASSWORD: ${KEYCLOAK_ADMIN_PASSWORD:?Set KEYCLOAK_ADMIN_PASSWORD in the root .env file}
      KC_HTTP_ENABLED: "true"
      KC_HOSTNAME_STRICT: "false"
    volumes:
      - ./deploy/keycloak/isestudio-realm.json:/opt/keycloak/data/import/isestudio-realm.json:ro
    ports:
      - "${KEYCLOAK_PORT:-8081}:8080"
    healthcheck:
      test: ["CMD-SHELL", "curl -f http://127.0.0.1:9000/health/ready || exit 1"]
      interval: 15s
      timeout: 5s
      retries: 20
    depends_on:
      keycloak-postgres:
        condition: service_healthy
    restart: unless-stopped
```

- [ ] **Step 2: backend 服务加 Keycloak 配置**

在 [docker-compose.yml:148](docker-compose.yml#L148)(`ISEStudio__PublicHost` 行)之后加:

```yaml
      # Keycloak SSO(JwtBearer,第 4 个 auth scheme)。Authority 是浏览器
      # 可见 URL —— token 的 iss 校验基准(Keycloak 按请求 Host 动态生成
      # issuer,与它一致);MetadataAddress 指容器内地址 —— 后端从这里拉
      # discovery + JWKS 验签(容器内无法解析 localhost:8081)。
      # 三者共享 root .env 的 ISE_AUTH_* 键,单处配置。
      ISEStudio__Auth__Keycloak__Authority: ${ISE_AUTH_AUTHORITY:-http://localhost:8081/realms/isestudio}
      ISEStudio__Auth__Keycloak__MetadataAddress: http://keycloak:8080/realms/isestudio/.well-known/openid-configuration
      ISEStudio__Auth__Keycloak__ClientId: ${ISE_AUTH_CLIENT_ID:-isestudio-frontend}
      ISEStudio__Auth__Keycloak__RequireHttpsMetadata: "false"
      ISEStudio__Auth__Keycloak__AdminRole: ${ISE_AUTH_ADMIN_ROLE:-admin}
```

- [ ] **Step 3: frontend 服务加注入 env**

在 [docker-compose.yml:171-178](docker-compose.yml#L171-L178) 的 `frontend` 服务加 environment:

```yaml
  frontend:
    build: ./frontend
    environment:
      # 前端 SSO 配置:容器 entrypoint 把它注入 index.html 的
      # window.__ISE_AUTH__(一镜像多环境的关键)。值必须浏览器可达——
      # 浏览器登录跳转与回调都走这个地址。
      ISE_AUTH_AUTHORITY: ${ISE_AUTH_AUTHORITY:-http://localhost:8081/realms/isestudio}
      ISE_AUTH_CLIENT_ID: ${ISE_AUTH_CLIENT_ID:-isestudio-frontend}
    depends_on:
      isestudio:
        condition: service_healthy
    ports:
      - "${ISESTUDIO_BIND_ADDRESS:-0.0.0.0}:${ISESTUDIO_PORT:-8080}:80"
    restart: unless-stopped
```

- [ ] **Step 4: volumes 加 keycloak 卷**

在 [docker-compose.yml:180-183](docker-compose.yml#L180-L183) 的 volumes 块加:

```yaml
volumes:
  isestudio-data:
  isestudio-postgres:
  isestudio-minio:
  isestudio-keycloak-postgres:
```

- [ ] **Step 5: root .env.example 加 Keycloak 段**

追加到 `.env.example` 末尾:

```bash
# --- Keycloak (SSO) ---------------------------------------------------------
# Keycloak 独立 Postgres 实例的密码(生产必改)。
KEYCLOAK_DB_PASSWORD=change-me-db-password
# Keycloak 管理控制台凭据(http://localhost:8081/admin 入口)。
KEYCLOAK_ADMIN_USER=admin
KEYCLOAK_ADMIN_PASSWORD=change-me-admin-password
# Keycloak 暴露到宿主(浏览器直连)的端口。
KEYCLOAK_PORT=8081
# 浏览器可访问的 Keycloak realm 地址 —— 后端(iss 校验)与前端(登录跳转)
# 共用同一键。生产改为 https://你的域名/auth 或独立 Keycloak 域名。
ISE_AUTH_AUTHORITY=http://localhost:8081/realms/isestudio
ISE_AUTH_CLIENT_ID=isestudio-frontend
# realm role 名:含此 role 的 SSO 用户 → 后端 IsAdmin=true。
ISE_AUTH_ADMIN_ROLE=admin
```

- [ ] **Step 6: compose 配置验证**

Run: `docker compose config --quiet && echo OK`
Expected: exit 0(若 `.env` 未建或 `KEYCLOAK_DB_PASSWORD` 等缺值,compose 会因 `:?` 报错——按报错提示补 root `.env`,或直接复制 `.env.example`)。注意:计划阶段 `docker compose config --quiet` 是解析校验,不需要服务真的起来。

- [ ] **Step 7: Commit**

```bash
git add docker-compose.yml .env.example
git commit -m "feat(sso): keycloak + keycloak-postgres as mandatory compose services

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: deploy/keycloak/isestudio-realm.json

**Files:**
- Create: `deploy/keycloak/isestudio-realm.json`

**Interfaces:**
- Consumes: Task 1 的 volume 挂载路径(`/opt/keycloak/data/import/isestudio-realm.json:ro`)。
- Produces: realm `isestudio`、public client `isestudio-frontend`(redirect 白名单)、realm role `admin`、演示用户 `sso-admin`(admin role)与 `sso-viewer`(无 role,验证 D4 默认无访问)。

- [ ] **Step 1: 写 realm 文件**

写 `deploy/keycloak/isestudio-realm.json` 全文:

```json
{
  "realm": "isestudio",
  "enabled": true,
  "sslRequired": "none",
  "registrationAllowed": false,
  "loginWithEmailAllowed": true,
  "duplicateEmailsAllowed": false,
  "resetPasswordAllowed": false,
  "clients": [
    {
      "clientId": "isestudio-frontend",
      "name": "ISEStudio Web",
      "enabled": true,
      "publicClient": true,
      "standardFlowEnabled": true,
      "directAccessGrantsEnabled": true,
      "redirectUris": [
        "http://localhost:8080/*",
        "http://127.0.0.1:8080/*"
      ],
      "webOrigins": [
        "http://localhost:8080",
        "http://127.0.0.1:8080"
      ],
      "fullScopeAllowed": true
    }
  ],
  "roles": {
    "realm": [
      {
        "name": "admin",
        "description": "ISEStudio admin — maps to IsAdmin=true on the synced user row"
      }
    ]
  },
  "users": [
    {
      "username": "sso-admin",
      "email": "admin@example.com",
      "enabled": true,
      "emailVerified": true,
      "firstName": "SSO",
      "lastName": "Admin",
      "credentials": [
        { "type": "password", "value": "change-me-sso" }
      ],
      "realmRoles": ["admin"]
    },
    {
      "username": "sso-viewer",
      "email": "viewer@example.com",
      "enabled": true,
      "emailVerified": true,
      "firstName": "SSO",
      "lastName": "Viewer",
      "credentials": [
        { "type": "password", "value": "change-me-sso" }
      ],
      "realmRoles": []
    }
  ]
}
```

**字段说明(写给操作者):**
- `sslRequired: "none"` —— http 自托管前提。Keycloak 26 仍接受该字段(可能打 deprecation 日志);生产 https 时删除此行。
- `publicClient: true` + 无 client secret —— 与后端 azp 校验配对(public client 的 aud 恒为 `account`,azp 才是真凭据)。
- `credentials[].value` 明文 —— `--import-realm` 导入时自动哈希;**生产必改**,或导入后进管理控制台改密。
- redirect 白名单 `localhost:8080` —— 生产改为实际前端域名。

- [ ] **Step 2: JSON 语法校验**

Run: `powershell -Command "Get-Content deploy/keycloak/isestudio-realm.json -Raw | ConvertFrom-Json | Out-Null; if ($?) { 'JSON OK' }"`
Expected: `JSON OK`

- [ ] **Step 3: Commit**

```bash
git add deploy/keycloak/isestudio-realm.json
git commit -m "feat(sso): isestudio realm — public client, admin role, demo users

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: frontend Dockerfile entrypoint 运行时注入

**Files:**
- Create: `frontend/docker-entrypoint.sh`
- Modify: `frontend/Dockerfile`

**Interfaces:**
- Consumes: Task 1 的 frontend env 键 `ISE_AUTH_AUTHORITY` / `ISE_AUTH_CLIENT_ID`;前端 authModel 的 `window.__ISE_AUTH__` 注入级(frontend 计划 Task 1)。
- Produces: 容器启动时把配置写进 `index.html`,前端读取后 `ssoEnabled()` 生效。无 env 时**不注入**——前端回落 `VITE_AUTH_*` 或禁用(SSO opt-in 保底)。

- [ ] **Step 1: 写 entrypoint**

写 `frontend/docker-entrypoint.sh` 全文:

```sh
#!/bin/sh
# 运行时把 Keycloak 配置注入 index.html 的 window.__ISE_AUTH__——
# 一个镜像走遍所有环境的关键(等价 goodcrew 后端 AuthConfigInjection,
# ISEStudio 后端不托管前端静态文件,注入点移到 nginx 容器)。
# 两个 env 都配了才注入;没配则不动 index.html,前端回落构建期
# VITE_AUTH_*(dev)或完全禁用(现有 cookie 登录路径)。
set -e

if [ -n "$ISE_AUTH_AUTHORITY" ] && [ -n "$ISE_AUTH_CLIENT_ID" ]; then
  # sed 替换文本里的 & 和 | 会破坏语法,先转义(URL 一般没有,防一手)。
  authority=$(printf '%s' "$ISE_AUTH_AUTHORITY" | sed 's/[&|\\]/\\&/g')
  clientId=$(printf '%s' "$ISE_AUTH_CLIENT_ID" | sed 's/[&|\\]/\\&/g')
  inject="<script>window.__ISE_AUTH__ = {\"authority\":\"${authority}\",\"clientId\":\"${clientId}\"};</script></head>"
  sed -i "s|</head>|${inject}|" /usr/share/nginx/html/index.html
fi

exec "$@"
```

- [ ] **Step 2: 改 Dockerfile**

把 [frontend/Dockerfile](frontend/Dockerfile) 全文替换为:

```dockerfile
# ISEStudio frontend — build the Vite app, then serve the static bundle + proxy /api via nginx.
FROM node:22-alpine AS build
WORKDIR /app
RUN corepack enable
COPY package.json pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY . .
RUN pnpm build

FROM nginx:alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
# 运行时把 ISE_AUTH_* 注入 index.html(Keycloak SSO 配置);无 env 则
# 原样放行,前端回落构建期配置或禁用 SSO。
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh
ENTRYPOINT ["/docker-entrypoint.sh"]
CMD ["nginx", "-g", "daemon off;"]
EXPOSE 80
```

- [ ] **Step 3: 本地脚本语法校验**

Run: `sh -n frontend/docker-entrypoint.sh && echo OK`
Expected: `OK`

- [ ] **Step 4: 容器冒烟(与 Task 4 的 compose 全栈一起验,这里先单独验注入)**

```bash
cd frontend && docker build -t isestudio-frontend-sso-check .
docker run --rm -e ISE_AUTH_AUTHORITY=http://keycloak.local:8081/realms/x -e ISE_AUTH_CLIENT_ID=my-client isestudio-frontend-sso-check sh -c 'grep -o "window.__ISE_AUTH__ = .*</script>" /usr/share/nginx/html/index.html'
```
Expected: 输出含 `window.__ISE_AUTH__ = {"authority":"http://keycloak.local:8081/realms/x","clientId":"my-client"};</script>`

再验无 env 路径:
```bash
docker run --rm isestudio-frontend-sso-check sh -c 'grep -c "__ISE_AUTH__" /usr/share/nginx/html/index.html || true'
```
Expected: `0`(未注入)

- [ ] **Step 5: Commit**

```bash
git add frontend/Dockerfile frontend/docker-entrypoint.sh
git commit -m "feat(sso): frontend entrypoint injects ISE_AUTH_* into index.html

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: 配置文档(src/.env.example + frontend/.env.example + README)

**Files:**
- Modify: `src/.env.example`(--- Auth --- 段)
- Modify: `frontend/.env.example`(末尾)
- Modify: `README.md`(部署章节)
- Modify: `README.zh-CN.md`(对应章节)

**Interfaces:** 无代码接口——纯文档,键名必须与 Task 1/2/3 完全一致。

- [ ] **Step 1: src/.env.example 加 Keycloak 键**

在 [src/.env.example:66](src/.env.example#L66)(`ISEStudio__CookieSecure=false` 之后)加:

```bash
# Keycloak SSO(JwtBearer,第 4 个 auth scheme)。Authority 为空 = SSO 整体
# 禁用(不注册 JwtBearer,现有登录行为不变)。compose 已自动配好;
# 本地跑(不经 compose)时手填。Authority 是浏览器可见 URL(iss 校验),
# MetadataAddress 可另指后端可达的 discovery 地址(容器内拉取用),
# 为空则默认从 Authority 派生。
ISEStudio__Auth__Keycloak__Authority=
ISEStudio__Auth__Keycloak__MetadataAddress=
ISEStudio__Auth__Keycloak__ClientId=isestudio-frontend
ISEStudio__Auth__Keycloak__RequireHttpsMetadata=true
ISEStudio__Auth__Keycloak__AdminRole=admin
```

- [ ] **Step 2: frontend/.env.example 加 VITE 键**

追加到 `frontend/.env.example` 末尾:

```bash
# Keycloak SSO(dev 用;生产走容器 entrypoint 注入 window.__ISE_AUTH__,
# 见 docker-compose.yml 的 frontend 服务与 frontend/docker-entrypoint.sh)。
# 两者都配了登录页才显示「使用 SSO 登录」按钮。
VITE_AUTH_AUTHORITY=http://localhost:8081/realms/isestudio
VITE_AUTH_CLIENT_ID=isestudio-frontend
```

- [ ] **Step 3: README.md 部署章节加 Keycloak SSO 小节**

在 README.md 的部署/快速开始章节(compose 说明之后)插入:

```markdown
### Keycloak SSO(可选开关,compose 必带服务)

`docker compose up -d --build` 会一并启动 Keycloak(宿主端口 `KEYCLOAK_PORT`,默认 8081)。
首次启动自动导入 realm `isestudio`(见 `deploy/keycloak/isestudio-realm.json`),登录页即出现
「使用 SSO 登录」按钮。

- **演示账号**:`sso-admin / change-me-sso`(realm role `admin` → 后端 `IsAdmin=true`);
  `sso-viewer / change-me-sso`(无 role → 默认无任何 KS 权限,由 admin 在成员页授权)。**生产必改**
- **管理控制台**:http://localhost:8081/admin(`KEYCLOAK_ADMIN_USER` / `KEYCLOAK_ADMIN_PASSWORD`)
- **SSO 与本地账号并存**:本地账号登录、session cookie、API/MCP token 全部不变;SSO 用户首次登录
  自动建本地用户行(用户名 `preferred_username`,与本地用户撞名时加 `~` 前缀后缀)
- **关闭 SSO**:把 root `.env` 的 `ISE_AUTH_AUTHORITY` 留空并重启——后端不注册 JwtBearer,
  前端不渲染 SSO 按钮,行为回到纯本地登录
- **切 https/生产域名**:改 `ISE_AUTH_AUTHORITY` + realm JSON 的 `redirectUris`/`webOrigins` +
  删 `sslRequired: "none"` + `RequireHttpsMetadata=true`
```

README.zh-CN.md 镜像段落(中文)。若 README 结构不同,按内容就近插入"部署"章。

- [ ] **Step 4: 键名一致性核查**

Run: `grep -rn "ISE_AUTH_AUTHORITY\|ISEStudio__Auth__Keycloak" docker-compose.yml .env.example src/.env.example frontend/.env.example`
Expected: 每处拼写完全一致(ISEStudio 前缀 + 双下划线分隔;前端键 ISE_AUTH_ 无后缀)。

- [ ] **Step 5: Commit**

```bash
git add src/.env.example frontend/.env.example README.md README.zh-CN.md
git commit -m "feat(sso): document Keycloak config keys in env examples + README

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: 全栈冒烟 + 收尾

**Files:** 无。

**前置:** 后端计划已全部完成(`SsoOptions.MetadataAddress` 存在),frontend 计划已全部完成。

- [ ] **Step 1: compose 配置门**

Run: `docker compose config --quiet && echo OK`
Expected: exit 0

- [ ] **Step 2: 起全栈**

```bash
cp .env.example .env  # 首次;已有 .env 则补 KEYCLOAK_* 段
docker compose up -d --build
docker compose ps
```
Expected: postgres / minio / keycloak-postgres / keycloak / isestudio-migrate / isestudio / frontend 全部 healthy/running。

- [ ] **Step 3: SSO 冒烟脚本**

1. 打开 `http://localhost:8080` → 登录页有「使用 SSO 登录」按钮
2. 点击 → 跳 Keycloak 登录页(URL 为 `ISE_AUTH_AUTHORITY` 前缀)
3. `sso-admin / change-me-sso` 登录 → 回调回 ISEStudio 首页,右上角用户为 sso-admin
4. DevTools Network:后续 API 请求带 `Authorization: Bearer`
5. Settings → Users 可访问(admin role → `IsAdmin=true` → AdminOnly 政策放行)
6. 登出 → 跳 Keycloak logout → 回首页,SSO 按钮仍在
7. 用 `sso-viewer / change-me-sso` 登录 → Settings 菜单无 Users/Models(默认无访问,D4)
8. 本地登录路径回归:登出后用户名/密码表单登录本地 admin 照常工作(D1 并存)
9. 数据库核查:主 postgres 的 `users` 表出现 `sso-admin` 行,`subject_id` 非空,`password_hash` 为空串

- [ ] **Step 4: SSO 禁用路径(opt-in 保底)**

```bash
docker compose stop
ISE_AUTH_AUTHORITY= docker compose up -d
```
Expected: 后端日志无 JwtBearer 注册、登录页无 SSO 按钮、本地登录一切照旧。完成后恢复 `ISE_AUTH_AUTHORITY` 并 `docker compose up -d`。

- [ ] **Step 5: 收尾 commit(若有 drift 修复)**

```bash
git status --short
# 干净则跳过;冒烟发现的问题修掉后单独 commit
```

---

## Self-Review

**Spec 覆盖**:§7.1 compose 服务 → Task 1(keycloak 26 / start --import-realm / KC_DB=postgres / depends_on keycloak-postgres / backend+frontend env);§7.2 realm 文件 → Task 2;§7.3 前端运行时注入 → Task 3(sed 方案替代 spec 写的 envsubst——nginx:alpine 无 gettext,免装包;注入语义相同);§7.4 配置文档 → Task 4;§6.3 `docker compose config --quiet` 门 → Task 1 Step 6 + Task 5 Step 1。§2 D6 必选 → 无 profile,`docker compose up` 默认带。

**spec 与实现的偏差(记录)**:spec §7.1 写 backend `Authority=http://keycloak:8080/...`——那是容器内地址,若照写,token iss 是浏览器 URL(`localhost:8081`),`ValidIssuer` 校验必失败。计划改为双 URL(Authority=浏览器 URL 做 iss 校验 + MetadataAddress=容器内地址拉 metadata),并在后端计划 Task 2/4 增加 `SsoOptions.MetadataAddress` 可选字段。这是执行期发现的 spec 设计缺口,已闭环。

**类型一致性**:env 键名三处一致(`ISEStudio__Auth__Keycloak__{Authority,MetadataAddress,ClientId,RequireHttpsMetadata,AdminRole}` ↔ 后端计划 `SsoOptions.SectionName`);前端注入键 `ISE_AUTH_*` ↔ compose frontend 环境 ↔ entrypoint ↔ authModel 注入级。

**执行顺序依赖**:Task 2 ← Task 1(挂载路径);Task 3 独立;Task 4 独立;Task 5 依赖后端计划 + frontend 计划全部完成 + Task 1-4。
