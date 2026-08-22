# Frontend ID 字段类型决策（string=Guid）ADR

**状态**: 已批准（追溯型，记录既有决策）
**日期**: 2026-08-23
**范围**: `frontend/src`(React + TypeScript strict)。后端不在本次范围（参见 [[ontopilot-guid-migration-complete]] 与 [[2026-08-20-guid-primary-key-design]]）。

---

## 1. 背景

2026-08-20 主 PR（参见 [[2026-08-20-guid-primary-key-design]]）将后端 wire 层主键从 `long LegacyId` 切换为 `Guid Id`。该 PR 的工作量预估包括前端 `id: number` → `id: string` 的大量改写（约 80 个 interface、20 个 route、约 50 个 URL builder）。然而实际 commit `f2c4aca`（docs 替换）与之前的多次 wire-side Guid commit 完成后，发现 **`frontend/src` 已经没有任何代码需要修改**。

本 ADR 追溯这一现象的成因、补固其约束，并定义检测未来的回归（避免有人不慎把 `id` 字段改回 `number`）。

### 1.1 决策事实

`frontend/src/lib/types.ts` 中每个 `id:`、`*_id:` 字段都已声明为 `string`——而非 `number`。这是**最初打地基时就设下的类型选择**，不是事后改造。

### 1.2 影响的直接好处

- 后端 int → Guid 切换对前端**完全透明**：0 runtime crash、0 类型签名破坏、0 URL 拼接差异、0 比较逻辑差异。
- 整套 Playwright e2e（`e2e/dotnet/*.spec.ts`）在 Guid 切换前后**没有任何场景需要调整**。

### 1.3 没有写出来的隐性约束

过去这份约束仅以"现状"形式存在，并没有被显式文档化。这导致：
- 后续 contributor 若用模板（如 `id: number`）反向添加新接口，会引入回归。
- 类型签名虽然正确，但 `lib/api.ts` 中 URL 拼接、`<SelectItem value>` 等位置未明确说明 Guid 的字符编码兼容性。

---

## 2. 决策

### 2.1 强约束

| 项 | 规则 |
|---|---|
| **id 字段类型** | `frontend/src/lib/types.ts` 中所有 `id`、`*_id` 字段一律 `string`，不得为 `number` |
| **URL path 拼接** | 用模板字面量直接拼接 `${id}`；不调用 `encodeURIComponent`（Guid 的 `[0-9a-f-]` 都是 RFC 3986 unreserved） |
| **useParams 类型** | 路由参数（`:id`、`:docId`、`:ksId`...）一律 `string`，不使用 `parseInt` 解析 |
| **parseInt/Number** | 禁止在 `.json()` 响应附近对 id 做 numeric coercion |
| **新对象添加** | 新建 `interface FooOut { ... }` 时如有 id 字段必须 `string`，不查后端 wire 也照此写 |

### 2.2 反 pattern（必须避免）

```typescript
// ❌ 不要写
setBusy(violation.id + fix.id)        // 巧合可用，但显式更稳
const uid = Number(user.id)            // id 不是数字
parseInt(ks.id)                        // id 不是数字
if (ks.id > 0) { ... }                 // Guid 无法比较
arr[ks.id]                             // id 不是数组下标

// ❌ 反向诱因
interface FooOut {
  id: number                            // ← 触发者会被 ADR 拦住
  legacy_id: number                      // ← legacy_id 已废弃
}
```

### 2.3 推荐模式

```typescript
// ✅ 推荐
setBusy(`${violation.id}::${fix.id}`) // 显式分隔符
`/api/knowledge/${ks.id}`             // 直接拼接，无 encode
useParams<{ id: string }>()           // 强制 string
typeof ks.id === 'string'             // 类型守卫
```

---

## 3. 实施

### 3.1 已完成（P1-1 ~ P1-3，本 ADR 落地）

| 时间 | 改动 | 位置 |
|---|---|---|
| 2026-08-23 | `setBusy(violation.id + fix.id)` → `` setBusy(`${violation.id}::${fix.id}`) `` | `frontend/src/components/ValidationPanel.tsx:87` |
| 2026-08-23 | `lib/types.ts` 头部加 ID CONVENTION 注释块 | `frontend/src/lib/types.ts:1-21` |
| 2026-08-23 | `lib/api.ts` 头部加 BACKEND + ID/URL CONVENTION 注释块 | `frontend/src/lib/api.ts:1-23` |

### 3.2 后续计划（不在本次 commit）

| 优先级 | 改动 |
|---|---|
| P2 | `frontend/src/pages/KnowledgePage.tsx:31` 的过期注释 `provider ids are >= 1` 替换为 `provider ids are non-empty strings` |
| P2 | `.env.example` 与 `vite.config.ts` backend proxy 注释补 Guid 说明 |
| P2 | Playwright e2e 新增 `assertGuidFormat(id)` helper |

---

## 4. 验证

### 4.1 静态扫描（CI 可加）

```bash
# 在 frontend/src/**/*.{ts,tsx} 内 grep 反 pattern
grep -rE '(parseInt|Number\(|toString\(\))' frontend/src/ \
  | grep -E '\b(id|legacy_id)\b'                          # 应为空
grep -rE 'id:\s*number\b' frontend/src/lib/types.ts      # 应为空
grep -rE 'legacy_id' frontend/src/                        # 应为空
```

### 4.2 动态回归

```bash
# Playwright e2e 跑全套，Guid 切换后无回归：
cd frontend && npx playwright test e2e/dotnet/
```

期望：所有 spec 通过；任何失败项与 Guid 切换无关（切后端 wire 范围由后端 PR 保证）。

### 4.3 添加新接口时的 checklist

PR 评审者对涉及 `interface FooOut { id: ... }` 的改动必须确认：

- [ ] `id` 字段类型是 `string`，不是 `number`
- [ ] 没有新引入 `legacy_id` 字段
- [ ] 没有 `parseInt(Number(...))` 处理响应的 id

---

## 5. 风险与权衡

| 风险 | 缓解 |
|---|---|
| 新 contributor 不读 ADR 直接 `id: number` | 头部注释 + CI grep 拦截 |
| 未来若再需切 wire 类型（如 ulid） | 仍维持 `string`，无破坏 |
| 当时未文档化的隐性约束让历史 commit 不可读 | 本 ADR 显式回填 |
| 品牌类型（branded `type KSId = string & { __brand }`）诱惑 | 当前**不引入**——增加模板阻力收益不抵。`string` 是规矩 |

---

## 6. 不在本次范围

- 不引入 branded type（保持 `string` 简单性）
- 不引入 `openapi-typescript` codegen（仅可选 P3）
- 不重写 `useParams` 返回类型（已经全 `string`）
- 不动后端（已是 Guid）

---

## 7. 参考

- [[2026-08-20-guid-primary-key-design]] — 主键统一到 Guid 的设计规格
- [[ontopilot-guid-migration-complete]] — 11 commits, 0 schema change, wire-side 完成
- [[ontopilot-dotnet-gap-2026-08-22]] — dotnet 迁移缺口核查
- `docs/superpowers/specs/2026-08-13-ontopilot-dotnet-migration-design.md` — .NET 迁移总设计
- `frontend/src/lib/types.ts:1-21` — ID CONVENTION 注释块
- `frontend/src/lib/api.ts:1-23` — BACKEND + ID/URL CONVENTION 注释块
- `frontend/src/components/ValidationPanel.tsx:87` — `+` → `` `${id}::${id}` `` 修改
