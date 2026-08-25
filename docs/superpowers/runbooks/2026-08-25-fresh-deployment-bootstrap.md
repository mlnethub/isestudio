# Fresh-deployment bootstrap(2026-08-25)

> 当 `docker compose up` 完成后,后端容器反复重启,日志里看到 `Bootstrap required: the users table is empty... Process will exit with code 17.`,且容器持续 `Restarting (N)` —— 这意味着这是**全新部署**,没有 admin 用户,需要手动 seed。

本文档是 .NET 后端 **ISEStudio** 的首次部署 runbook。Python 时代通过 `ADMIN_PASSWORD` 环境变量自动 seed admin; .NET 启动期 `BootstrapAdminService` **故意拒绝**自动 bootstrap(避免空装意外落入已知弱凭证),强制运维显式种子第一个管理员([src/ISEStudio/Infrastructure/Startup/BootstrapAdminService.cs:118-160](../ISEStudio/Infrastructure/Startup/BootstrapAdminService.cs))。

---

## 1. 触发条件

满足以下**全部**条件时,按本 runbook 操作:

- `docker compose up -d` 完成
- `docker compose ps` 显示后端服务(`isestudio`)状态 `Restarting (N) ...`,每次重启间隔几秒
- `docker compose logs isestudio --tail=20` 出现关键行:
  ```
  [FTL] Bootstrap required: the users table is empty. ISEStudio refuses to
        auto-create a default admin user (no default password). Connect to
        the running pod (SSH, kubectl exec, etc.) and seed the first
        administrator manually. Process will exit with code 17.
  ```
- exit code = **17**(`BootstrapAdminService.BootstrapRequiredExitCode`,见 [src/ISEStudio/Infrastructure/Startup/BootstrapAdminService.cs:64](../ISEStudio/Infrastructure/Startup/BootstrapAdminService.cs))

**不要**误判为其他问题 — exit code 17 + 上述日志签名就是 bootstrap 路径,继续往下走即可。

---

## 2. 先决条件

| 工具 | 用途 | 校验 |
|---|---|---|
| `docker` + `docker compose` | 容器编排 | `docker compose ps` 能看到 `postgres` + `isestudio-migrate`(Exited 0)+ `isestudio`(Restarting) |
| `psql` 或 `docker exec ... psql` | 在 postgres 容器内执行 SQL | postgres 容器 healthy(`STATUS = Up ... (healthy)`) |
| `python` 3.x with `bcrypt` | 生成 BCrypt 密码 hash | `python -c "import bcrypt; print(bcrypt.__version__)"` 不报错 |

> postgres 容器通常没有 psql 在 host 上,用 `docker exec -i ontopilot-postgres-1 psql ...` 即可。容器名是 `<project-prefix>_postgres-1`,project prefix 默认等于 `docker-compose.yml` 所在目录名(当前仓库 `ontopilot/`,所以是 `ontopilot-postgres-1`)。

---

## 3. 操作步骤

### 3.1 停止后端的 restart loop

后端在反复重启(seed 之前每改一次 SQL 都要重启,改之前先停):

```bash
docker compose stop isestudio
```

确认:

```bash
docker compose ps isestudio
# STATUS 应为 Stopped / Exited,不再 Restarting
```

### 3.2 生成 BCrypt 密码 hash

**只用 BCrypt(cost=12)**,不要用 sha256 / sha512 / md5 / 明文。密码服务实现是 `BCrypt.Net.BCrypt.HashPassword(pwd, 12)`,登录校验走 `BCrypt.Net.BCrypt.Verify`,只要 hash 格式是 `$2a$12$...` / `$2b$12$...` / `$2y$12$...` 中任一种就能 verify。

Python 一行搞定(密码替换为你的目标密码):

```bash
python -c "import bcrypt; print(bcrypt.hashpw(b'ChangeMe-ISEStudio-2026', bcrypt.gensalt(12)).decode())"
```

输出形如:

```
$2b$12$AC7V9A6arAVLenhz0aGMUe.6HXK1NIdMSOUSJbtOe7J1BDKBol/sK
```

**复制这串 hash,下一步 INSERT 用。**

密码约束(`PasswordService.Validate`,见 [src/ISEStudio/Authentication/PasswordService.cs:90-109](../ISEStudio/Authentication/PasswordService.cs)):

- 最少 **12 字符**
- UTF-8 字节长度 ≤ **72**(BCrypt 硬上限,超过必拒)
- 不在 bootstrap 黑名单:`admin` / `admin123` / `change-me` / `changeme` / `password` / `replace-with-a-strong-password`(大小写不敏感)
- 黑名单仅在 **bootstrap 路径**生效(seed 之后改密码不受此约束)

### 3.3 查清楚 users 表的实际列名

EF Core 10 + Npgsql 10 默认**保留 PascalCase 列名**(加了双引号)。`users` 表里 `Username` / `PasswordHash` / `IsAdmin` 等是 PascalCase,`id` / `legacy_id` 是 snake_case(EF 显式 `HasColumnName` 指定过)。**混合大小写** —— 必须先看实际 schema:

```bash
docker exec ontopilot-postgres-1 psql -U isestudio -d isestudio -c '\d users'
```

输出关键部分:

```
     Column     |           Type           | Nullable | Default
----------------+--------------------------+----------+---------
 id             | uuid                     | not null |
 Username       | character varying(255)   | not null |           <-- PascalCase!
 DisplayName    | character varying(255)   |          |
 PasswordHash   | character varying(255)   | not null |
 IsAdmin        | boolean                  | not null | false
 Active         | boolean                  | not null | true
 CreatedAt      | timestamp with time zone | not null |
 legacy_id      | bigint                   | not null |           <-- snake_case
```

如果你的表**全是 snake_case**(说明 schema 是手写的,不是 EF 迁移来的),把下文的双引号去掉直接写即可;但当前 ISEStudio schema 是混合,必须按本 runbook 的双引号写法。

### 3.4 确认 postgres 凭据

```bash
grep -E "POSTGRES_(DB|USER)" docker-compose.yml
# 期望:POSTGRES_DB: isestudio / POSTGRES_USER: isestudio
grep '^POSTGRES_PASSWORD=' .env
# 期望:POSTGRES_PASSWORD=<some-value>
```

`POSTGRES_PASSWORD` 不直接传给 psql(容器内 trust auth 用 peer / `POSTGRES_USER`)。host 上 `docker exec ... psql -U isestudio` 走 socket + 同用户映射,无需密码。

### 3.5 手工 INSERT admin

把 `<BCRYPT_HASH>` 替换为 3.2 生成的 hash:

```bash
docker exec -i ontopilot-postgres-1 psql -U isestudio -d isestudio -v ON_ERROR_STOP=1 <<'SQL'
INSERT INTO users (
    id,
    "Username",
    "DisplayName",
    "PasswordHash",
    "IsAdmin",
    "Active",
    "CreatedAt",
    legacy_id
)
VALUES (
    gen_random_uuid(),
    'admin',
    NULL,
    '<BCRYPT_HASH>',
    true,
    true,
    NOW(),
    COALESCE((SELECT MAX(legacy_id) FROM users), 0) + 1
)
RETURNING id, "Username", "IsAdmin", "Active", legacy_id, "CreatedAt";
SQL
```

预期输出(具体值会变):

```
                  id                  | Username | IsAdmin | Active | legacy_id |       CreatedAt
--------------------------------------+----------+---------+--------+-----------+---------------------
 d1d4a265-922c-471b-98d5-a029db926ebb | admin    | t       | t      |         1 | 2026-08-25 15:15:22+00
(1 row)

INSERT 0 1
```

> `legacy_id` 必须由 allocator 派发(全栈唯一,后续 `LegacyIdAllocator.AllocateAndPersistAsync` 会从 `MAX(legacy_id) + 1` 读)。这里手动用 `COALESCE(MAX + 1)` 保证首次分配 = 1,后续 `LegacyIdAllocator` 顺接即可。**不要**自己写固定值(如 100 / 1000),会和 allocator 抢号。

### 3.6 重启后端,看 bootstrap 通过

```bash
docker compose up -d isestudio
```

等几秒后:

```bash
docker compose ps isestudio
# STATUS 应为:Up N seconds (healthy)

docker compose logs isestudio --tail=10 | grep -i "bootstrap"
# 预期:
# [INF] Bootstrap check passed: 1 user row(s) present.
```

如果还看到 `Bootstrap required: ...` —— 检查上一条 INSERT 是否真的写进去了(`SELECT count(*) FROM users;` 应该返 1),以及 hash 是否被引号正确包住(转义出错会导致 hash 被截断,verify 失败但 bootstrap 检查是通过的——见 §4)。

### 3.7 验证登录

```bash
curl -sS -X POST http://127.0.0.1:8080/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"ChangeMe-ISEStudio-2026"}' -i
```

预期:

```
HTTP/1.1 200 OK
Set-Cookie: isestudio_session=<...>; max-age=1209600; path=/; samesite=lax; httponly

{"id":"<uuid>","username":"admin","display_name":null,"is_admin":true,"active":true}
```

(注意 cookie 名是 **`isestudio_session`** —— 这是 rename 后的标准 cookie 名,见 `ISEStudioOptions.SessionCookie`。如果看到 `ontopilot_session`,说明部署的不是 post-rename 构建。)

---

## 4. 常见踩坑

### 4.1 `column "username" of relation "users" does not exist`

INSERT 没加双引号,EF Core 的 mixed-case 标识符被 PG 折成小写,找不到列。**必须**写 `"Username"` 而不是 `username`。

### 4.2 `column "is_admin" of relation "users" does not exist`

同上 —— `"IsAdmin"` 加双引号。

### 4.3 login 返 401 `Invalid credentials`

- 用户存在(`SELECT count(*) FROM users WHERE "Username" = 'admin';` 返 1)
- 但 hash 不对 —— 可能是 shell 转义把 `$` 吃掉(尤其在 `<<EOF` 而不是 `<<'EOF'` 里),或 hash 被截断。
- 验证:`docker exec ontopilot-postgres-1 psql -U isestudio -d isestudio -c 'SELECT "PasswordHash" FROM users WHERE "Username" = '\''admin'\'';'`
  - 应该看到 60 字符的 BCrypt hash(`$2[aby]$12$` 开头 + 53 字符)
  - 如果长度 < 60:重新 INSERT 一条

### 4.4 后端还是 Restarting,日志不是 bootstrap 而是别的

不是本 runbook 场景。看完整日志:

```bash
docker compose logs isestudio --tail=80 | grep -E "FTL|ERR|Exception"
```

常见其他原因(本 runbook 不覆盖):

- postgres 连接串错(检查 `ISEStudio__Persistence__ConnectionString`)
- DB 不存在(`FATAL: database "isestudio" does not exist`)
- `isestudio-migrate` 没成功跑(应该 `Exited (0)`)
- EF 迁移版本错配(`A migration was applied that downgrades the database`)

### 4.5 `gen_random_uuid()` 不存在

postgres 13 以下没装 `pgcrypto`。换 `uuid_generate_v4()`(需要 `CREATE EXTENSION IF NOT EXISTS "uuid-ossp";`)或从 host 生成:

```bash
python -c "import uuid; print(uuid.uuid4())"
```

ISEStudio 镜像锁 `postgres:16-alpine`,`gen_random_uuid()` 是 13+ 内置函数,不会触发这条;只在手动降级到 12 或更老时才需要。

### 4.6 旧 volume 里有数据,不想丢

如果 `isestudio-postgres` volume 是空的但 `ontopilot_ontopilot-postgres`(rename 前的 volume)还在,数据可能完整保留在旧 volume 里。**两种选择**:

- **A. 直接接回旧 volume**:改 `docker-compose.yml` 把 `source: isestudio-postgres` 改成 `source: ontopilot-postgres`(引用旧 volume 的实际名),`docker compose up -d` 重跑 migrate + backend。**所有数据原样回来**。
- **B. pg_dump / pg_restore 跨 volume 灌**:干净但慢,适合想保留 `isestudio-postgres` 命名语义。

本 runbook 只覆盖**全新装**场景,数据迁移请参考 docs/migration/ 下存量迁移工具(`iri-sql-migrate` / `rdf-migrate` / `blob-migrate`)。

---

## 5. 后续步骤

seed 完成后:

1. **立即改密码** —— 默认 `ChangeMe-ISEStudio-2026` 仅用于首次登录,必须走 admin UI 或 `PATCH /api/admin/users/{id}` 改掉。`PasswordService.Validate` 在非 bootstrap 路径不查黑名单,但仍要求 ≥ 12 字符。
2. **创建第二个 admin** —— `BootstrapAdminService` 检查的是 *任意* user 存在,所以一个 admin 就足以解锁启动;但生产应该有冗余 admin(防止单点丢失)。用 admin UI 或 API 创建。
3. **写一笔 audit** —— `bootstrap` 操作本身不需要进 audit(没有 actor),但 seed 之后**第一次登录**会被 `AuthService` 写一条 `auditevent`(`action = 'login'`,`actor_id = <admin uuid>`),供合规追溯。
4. **关闭 0 用户启动保护?** —— 不建议。`BootstrapAdminService` 的 fail-closed 设计是[架构决策(见 spec §7 D1)](../specs/2026-08-25-isestudio-rename-design.md),意图明确。如果业务侧要"首次启动自动 seed",需要先**单独提一个 ADR** 推翻该决策,而不是绕过这个 runbook。

---

## 6. Lesson learned(2026-08-25 第一次触发本场景时记下)

- **Stage 3 territory miss**:brand rename 切片(commit `e8c8d02` + follow-up `df1bcb3`)没把"deployment bootstrap procedure"列为 verification gate;首次部署时才在 exit 17 上摔了一次。后续 brand 切片 / 重命名切片应在 spec §7 gates 加一条 "fresh deploy smoke test",完整跑通本 runbook 1→6 步才算切片完成。
- **静态检测 vs 运行时**:本场景**单元测试 / 契约测试 / 集成测试都无法触发** —— 它们都在跑测试 fixture 的 DbContext,fixture 里通常会 seed 一个 admin。BootstrapAdminService 的 fail-closed 行为只在 "真实空 users 表" + "真实容器化启动" 这两个条件下才会触发。**只有 docker compose up smoke test 能抓**。
- **Postgres mixed-case 列名** 是不显眼但稳定的坑:EF Core 默认行为就是 PascalCase 透传,跨 schema 手工 SQL 时要主动加双引号。本 runbook §3.5 直接给完整 INSERT 模板就是为了让运维不踩。
- **BCrypt 跨语言兼容**:Python `bcrypt` 默认产 `$2b$`,C# `BCrypt.Net` 完全兼容 verify(`.NET BCrypt.Verify` 接受 `$2a$` / `$2b$` / `$2y$` 三种 prefix)。反过来 C# 产 `$2a$` Python 也能 verify。所以混合栈里 Python 临时生成 hash 是合法操作。
- **Pyhon 退役后第一次看到 exit 17**:Python 时代靠 `ADMIN_PASSWORD` 自动 seed,运维肌肉记忆是"设环境变量"。.NET 时代改成"手工 SQL"。**不是 bug,是设计** —— 文档化比绕过重要。

---

## 7. 链接

- 实现:`src/ISEStudio/Infrastructure/Startup/BootstrapAdminService.cs`
- 密码服务:`src/ISEStudio/Authentication/PasswordService.cs`
- AuthService 用户创建路径(参考 INSERT 列顺序):`src/ISEStudio/Authentication/AuthService.cs:130-160`
- EF model snapshot(`users` 表定义):`src/ISEStudio/Infrastructure/Persistence/Migrations/ISEStudioDbContextModelSnapshot.cs:1832-1883`
- 触发本 runbook 的命名切片:[[ontopilot-isestudio-rename]] §"Post-slice follow-up"
- 上游 Python 退役:[[ontopilot-python-retirement]]
