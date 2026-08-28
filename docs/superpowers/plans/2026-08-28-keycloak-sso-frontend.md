# Keycloak SSO 前端实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 frontend 增加手写 OIDC(无 SDK)的 Keycloak SSO 登录:登录页加 SSO 按钮,认证后所有 API 请求带 Bearer token,401 自动回 Keycloak 重登;SSO 未配置时行为逐字节不变。

**Architecture:** 照搬 goodcrew-pro 前端手写 OIDC 模式(授权码流程无 PKCE、state 事务 15min TTL、60s 自动重登冷却闸、refresh 静默续期),去掉全部组织(X-Organization / orgs / keycloakReachable)逻辑。纯函数层 [authModel.ts](frontend/src/lib/sso/authModel.ts) + 状态机层 [auth.ts](frontend/src/lib/sso/auth.ts),三级配置:`window.__ISE_AUTH__`(容器 entrypoint 注入)> `VITE_AUTH_*`(dev 构建期)> 未配置(= SSO 禁用,现有 cookie 路径不动)。token 存 sessionStorage,`lib/api.ts` 的 `request()` 统一注入 `Authorization: Bearer`。

**Tech Stack:** Vite 8 / React 19 / TypeScript 6 / vitest(新引入,唯一新依赖)/ oxlint / pnpm。

**Spec:** [2026-08-28-keycloak-sso-design.md](../specs/2026-08-28-keycloak-sso-design.md)(§5 前端设计、§6.2 前端测试、§2 D1/D5/D7)

## Global Constraints

- 无新运行时依赖(vitest 是 devDependency;不引入 keycloak-js)
- SSO 未配置(无注入且无 `VITE_AUTH_*`):`ssoEnabled()` false,登录页不渲染 SSO 按钮,`api.ts` 不加任何 header,现有 e2e 全绿
- `pnpm lint`(oxlint)0 error;`pnpm build`(tsc -b + vite build)0 error
- i18n:新键必须 en + zh-CN 双份(`MessageKey = keyof typeof en`,tsc 强制镜像)
- 注释英文(代码内解释性注释),与现有代码库一致
- 提交风格:`feat(sso): ...` + 尾随 `Co-Authored-By: Claude <noreply@anthropic.com>`
- 模块化陷阱:模块顶层常量 import 时求值——测试三级配置必须 `vi.resetModules()` + 动态 import

---

### Task 1: vitest 基建 + authModel.ts(TDD)

**Files:**
- Create: `frontend/src/lib/sso/authModel.ts`
- Create: `frontend/src/lib/sso/authModel.test.ts`
- Modify: `frontend/package.json`(devDependency + test script)

**Interfaces:**
- Produces(被 Task 2/3/4 消费):`AUTHORITY` / `CLIENT_ID`(string 常量)、`ssoEnabled(): boolean`、`AUTH_ENDPOINT` / `TOKEN_ENDPOINT` / `LOGOUT_ENDPOINT`、`buildAuthUrl(redirectUri: string, state: string): string`、`buildLogoutUrl(redirectUri: string, idToken: string): string`、`parseCallback(search: string): { code: string; state: string } | null`(error 参数存在时**抛错**)、`needsRefresh(expiresAtMs: number, nowMs: number): boolean`(60s skew)、`randomState(): string`(32 hex)

- [ ] **Step 1: 安装 vitest 并加 test script**

Run: `cd frontend && pnpm add -D vitest`
(若 vitest 与 vite 8 存在 peer 冲突,退回 `pnpm add -D vitest@^3`。authModel 是纯函数测试,`environment` 用默认 `node`,不需要 jsdom。)

在 `frontend/package.json` 的 `scripts` 里加:

```json
    "test": "vitest run",
```

- [ ] **Step 2: 写失败测试**

写 `frontend/src/lib/sso/authModel.test.ts` 全文:

```ts
// authModel 的模块顶层常量在 import 时求值,三级配置测试必须重置
// 模块再动态 import。globalThis.__ISE_AUTH__ 模拟容器 entrypoint 注入。
import { afterEach, describe, expect, it, vi } from "vitest"

async function loadModel(injected?: { authority?: string; clientId?: string }) {
  vi.resetModules()
  if (injected) {
    ;(globalThis as { __ISE_AUTH__?: unknown }).__ISE_AUTH__ = injected
  } else {
    delete (globalThis as { __ISE_AUTH__?: unknown }).__ISE_AUTH__
  }
  return import("./authModel")
}

afterEach(() => {
  vi.unstubAllEnvs()
  delete (globalThis as { __ISE_AUTH__?: unknown }).__ISE_AUTH__
})

describe("authModel", () => {
  it("未配置时 ssoEnabled 为 false", async () => {
    vi.stubEnv("VITE_AUTH_AUTHORITY", "")
    vi.stubEnv("VITE_AUTH_CLIENT_ID", "")
    const m = await loadModel()
    expect(m.ssoEnabled()).toBe(false)
  })

  it("注入配置优先于构建期变量", async () => {
    vi.stubEnv("VITE_AUTH_AUTHORITY", "https://vite-authority/realms/x")
    vi.stubEnv("VITE_AUTH_CLIENT_ID", "vite-client")
    const m = await loadModel({ authority: "https://injected/realms/x", clientId: "injected-client" })
    expect(m.AUTHORITY).toBe("https://injected/realms/x")
    expect(m.CLIENT_ID).toBe("injected-client")
    expect(m.ssoEnabled()).toBe(true)
  })

  it("构建期变量作为第二级", async () => {
    vi.stubEnv("VITE_AUTH_AUTHORITY", "https://vite-authority/realms/x")
    vi.stubEnv("VITE_AUTH_CLIENT_ID", "vite-client")
    const m = await loadModel()
    expect(m.AUTHORITY).toBe("https://vite-authority/realms/x")
    expect(m.CLIENT_ID).toBe("vite-client")
    expect(m.ssoEnabled()).toBe(true)
  })

  it("只配一半不算启用", async () => {
    vi.stubEnv("VITE_AUTH_AUTHORITY", "https://vite-authority/realms/x")
    vi.stubEnv("VITE_AUTH_CLIENT_ID", "")
    const m = await loadModel()
    expect(m.ssoEnabled()).toBe(false)
  })

  it("buildAuthUrl 拼出授权参数(无 PKCE 无 code_challenge)", async () => {
    const m = await loadModel({ authority: "https://kc/realms/isestudio", clientId: "isestudio-frontend" })
    const url = m.buildAuthUrl("http://localhost:8080/", "state-123")
    const u = new URL(url)
    expect(`${u.origin}${u.pathname}`).toBe("https://kc/realms/isestudio/protocol/openid-connect/auth")
    const q = u.searchParams
    expect(q.get("client_id")).toBe("isestudio-frontend")
    expect(q.get("response_type")).toBe("code")
    expect(q.get("redirect_uri")).toBe("http://localhost:8080/")
    expect(q.get("state")).toBe("state-123")
    expect(q.get("scope")).toBe("openid profile email")
    expect(q.has("code_challenge")).toBe(false)
  })

  it("parseCallback:error 时抛错而不是当未登录", async () => {
    const m = await loadModel()
    expect(() => m.parseCallback("?error=access_denied&error_description=nope")).toThrow(/access_denied/)
  })

  it("parseCallback:无 code 返 null", async () => {
    const m = await loadModel()
    expect(m.parseCallback("")).toBeNull()
  })

  it("parseCallback:有 code 返回 code+state", async () => {
    const m = await loadModel()
    expect(m.parseCallback("?code=abc&state=s1")).toEqual({ code: "abc", state: "s1" })
  })

  it("needsRefresh:剩余 60s 整需要换,61s 不需要,已过期需要", async () => {
    const m = await loadModel()
    const now = 1_000_000
    expect(m.needsRefresh(now + 60_000, now)).toBe(true)
    expect(m.needsRefresh(now + 60_001, now)).toBe(false)
    expect(m.needsRefresh(now - 1, now)).toBe(true)
  })

  it("buildLogoutUrl 带 id_token_hint 与回跳地址", async () => {
    const m = await loadModel({ authority: "https://kc/realms/isestudio", clientId: "c" })
    const q = new URL(m.buildLogoutUrl("http://localhost:8080/", "id-token-1")).searchParams
    expect(q.get("id_token_hint")).toBe("id-token-1")
    expect(q.get("post_logout_redirect_uri")).toBe("http://localhost:8080/")
  })

  it("randomState 是 32 位十六进制且两次不同", async () => {
    const m = await loadModel()
    const a = m.randomState()
    const b = m.randomState()
    expect(a).toMatch(/^[0-9a-f]{32}$/)
    expect(b).toMatch(/^[0-9a-f]{32}$/)
    expect(a).not.toBe(b)
  })
})
```

- [ ] **Step 3: Run 验证失败**

Run: `cd frontend && pnpm test`
Expected: FAIL(`Cannot find module './authModel'`)

- [ ] **Step 4: 写实现**

写 `frontend/src/lib/sso/authModel.ts` 全文:

```ts
// Keycloak OIDC 的纯函数部分(spec §5.2)。与 auth.ts 分开是为了可测:
// 这里没有 window / storage / 网络,全部是输入到输出。

// 登录域三级取值:容器 entrypoint 写进 index.html 的 > 构建期变量 > 未配置。
// 未配置 = SSO 禁用:登录页不渲染 SSO 按钮,api.ts 原样走 cookie。
// 注入级是让一个镜像走遍所有环境的关键(见 deploy 计划的 entrypoint 脚本);
// 没有它就只能把 realm 编译进包里,配错时表现只是「令牌不对」。
const injected = (globalThis as {
  __ISE_AUTH__?: { authority?: string; clientId?: string }
}).__ISE_AUTH__

export const AUTHORITY =
  injected?.authority?.trim() || import.meta.env.VITE_AUTH_AUTHORITY?.trim() || ""
export const CLIENT_ID =
  injected?.clientId?.trim() || import.meta.env.VITE_AUTH_CLIENT_ID?.trim() || ""

/** SSO 是否启用:authority 与 clientId 都配了才算启用。 */
export function ssoEnabled(): boolean {
  return Boolean(AUTHORITY && CLIENT_ID)
}

const OIDC = `${AUTHORITY}/protocol/openid-connect`
export const AUTH_ENDPOINT = `${OIDC}/auth`
export const TOKEN_ENDPOINT = `${OIDC}/token`
export const LOGOUT_ENDPOINT = `${OIDC}/logout`

// 到期前这么久就提前换新:正好卡在边界发出的请求不能带着将死的 token
const REFRESH_SKEW_MS = 60_000

/**
 * 授权跳转 URL。**不带 PKCE**(spec §2 D7):自托管常为 http://,
 * 非安全上下文下 crypto.subtle 不可用,S256 算不出来;plain 在能截获
 * redirect 的攻击者面前等于没有。state 事务校验挡住跨站伪造。
 */
export function buildAuthUrl(redirectUri: string, state: string): string {
  const q = new URLSearchParams({
    client_id: CLIENT_ID,
    response_type: "code",
    redirect_uri: redirectUri,
    state,
    scope: "openid profile email",
  })
  return `${AUTH_ENDPOINT}?${q}`
}

export function buildLogoutUrl(redirectUri: string, idToken: string): string {
  const q = new URLSearchParams({
    id_token_hint: idToken,
    post_logout_redirect_uri: redirectUri,
  })
  return `${LOGOUT_ENDPOINT}?${q}`
}

/** 解析回调 query。Keycloak 报错时抛——静默当作未登录会跳回去,变成死循环。 */
export function parseCallback(search: string): { code: string; state: string } | null {
  const q = new URLSearchParams(search)
  const error = q.get("error")
  if (error) throw new Error(`Keycloak 拒绝授权: ${error} ${q.get("error_description") ?? ""}`.trim())
  const code = q.get("code")
  if (!code) return null
  return { code, state: q.get("state") ?? "" }
}

export function needsRefresh(expiresAtMs: number, nowMs: number): boolean {
  return expiresAtMs - nowMs < REFRESH_SKEW_MS
}

/** state 用 getRandomValues —— 它在 http 非安全上下文下也可用,不像 crypto.subtle。 */
export function randomState(): string {
  const b = new Uint8Array(16)
  crypto.getRandomValues(b)
  return Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("")
}
```

- [ ] **Step 5: Run 验证通过**

Run: `cd frontend && pnpm test`
Expected: 11 passed

- [ ] **Step 6: Commit**

```bash
git add frontend/src/lib/sso/authModel.ts frontend/src/lib/sso/authModel.test.ts frontend/package.json frontend/pnpm-lock.yaml
git commit -m "feat(sso): OIDC pure-function layer (authModel) + vitest setup

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: sso/auth.ts 状态机

**Files:**
- Create: `frontend/src/lib/sso/auth.ts`

**Interfaces:**
- Consumes: Task 1 的全部导出。
- Produces(被 Task 3/4 消费):`login(): void`、`restartLogin(): void`、`logout(): void`(带 id_token_hint 跳 Keycloak)、`hasSession(): boolean`、`clearTokens(): void`、`ensureAuthenticated(): Promise<void>`(回调不匹配 reject `Error(reason)`,reason ∈ `"no_state" | "unknown_transaction" | "expired" | "already_exchanged"`)、`getAccessToken(): Promise<string | null>`(refresh 失败返 null,**不**触发跳转——重登由 401 handler 负责)

**说明(为什么不写单测):** 本文件全部依赖 sessionStorage / localStorage / location / history / fetch(Keycloak 端点)——照搬 goodcrew-pro 成熟实现,它的行为已被生产验证;这些状态机路径的回归由 e2e(有 Keycloak 环境的冒烟,deploy 计划 §5)覆盖。spec §6.2 只要求 authModel 单测。

- [ ] **Step 1: 写实现**

写 `frontend/src/lib/sso/auth.ts` 全文:

```ts
// Keycloak 登录状态机(spec §5.3)。纯函数在 authModel.ts。
//
// 所有带鉴权的 API 调用都由 lib/api.ts 注入 token;这里只负责
// 「什么时候有 token、什么时候去换」:启动处理回调、refresh 静默续期、
// 401 时清干净重登、logout 跳 Keycloak。

import {
  CLIENT_ID, TOKEN_ENDPOINT,
  buildAuthUrl, buildLogoutUrl, needsRefresh, parseCallback, randomState,
} from "./authModel"

const K = {
  access: "isestudio_at",
  refresh: "isestudio_rt",
  id: "isestudio_it",
  exp: "isestudio_exp",
} as const

type LoginTransaction = {
  returnTo: string
  createdAt: number
  exchangeStarted: boolean
}

const LOGIN_TRANSACTION_PREFIX = "isestudio_login_"
const LOGIN_TRANSACTION_TTL_MS = 15 * 60 * 1000

// 只限当前页面生命周期:同一 state 正在换票时复用同一个 Promise;刷新后此内存表
// 自然消失,不能承诺复用授权码,因为浏览器无法判断身份服务是否已经消费它。
const inFlightCallbackExchanges = new Map<string, Promise<void>>()

function transactionKey(state: string): string {
  return `${LOGIN_TRANSACTION_PREFIX}${state}`
}

function removeExpiredTransactions(now: number): void {
  for (let index = sessionStorage.length - 1; index >= 0; index -= 1) {
    const key = sessionStorage.key(index)
    if (!key?.startsWith(LOGIN_TRANSACTION_PREFIX)) continue
    try {
      const raw = sessionStorage.getItem(key)
      const transaction = raw ? JSON.parse(raw) as LoginTransaction : null
      if (!transaction || now - transaction.createdAt >= LOGIN_TRANSACTION_TTL_MS) {
        sessionStorage.removeItem(key)
      }
    } catch {
      sessionStorage.removeItem(key)
    }
  }
}

// 回调对不上有四种完全不同的原因。原因必须分开、必须能摆到人眼前——
// 共用一句「state 不匹配」时现场不留任何可区分的痕迹(诊断教训)。
// 用稳定 code 抛给 UI,文案由 i18n 翻译。
type CallbackRejection = "no_state" | "unknown_transaction" | "expired" | "already_exchanged"

type CallbackCheck =
  | { ok: true; transaction: LoginTransaction }
  | { ok: false; reason: CallbackRejection }

function lookupTransaction(state: string, now: number): CallbackCheck {
  const raw = sessionStorage.getItem(transactionKey(state))
  if (!raw) return { ok: false, reason: "unknown_transaction" }
  let transaction: LoginTransaction
  try {
    transaction = JSON.parse(raw) as LoginTransaction
  } catch {
    sessionStorage.removeItem(transactionKey(state))
    return { ok: false, reason: "unknown_transaction" }
  }
  if (now - transaction.createdAt >= LOGIN_TRANSACTION_TTL_MS) {
    sessionStorage.removeItem(transactionKey(state))
    return { ok: false, reason: "expired" }
  }
  // 已经开始换票的记录留到过期为止(浏览器无法确认身份服务是否已消费授权码),不在这里删。
  if (transaction.exchangeStarted !== false) return { ok: false, reason: "already_exchanged" }
  return { ok: true, transaction }
}

// 拒绝回调时打一份现场:这个页签手上还有几笔登录记录、各自多久之前建的。
function transactionDigest(now: number): Array<Record<string, unknown>> {
  const digest: Array<Record<string, unknown>> = []
  for (let index = 0; index < sessionStorage.length; index += 1) {
    const key = sessionStorage.key(index)
    if (!key?.startsWith(LOGIN_TRANSACTION_PREFIX)) continue
    const state = key.slice(LOGIN_TRANSACTION_PREFIX.length).slice(0, 8)
    try {
      const transaction = JSON.parse(sessionStorage.getItem(key) ?? "") as LoginTransaction
      digest.push({ state, ageMs: now - transaction.createdAt, exchangeStarted: transaction.exchangeStarted })
    } catch {
      digest.push({ state, unparsable: true })
    }
  }
  return digest
}

// 回调对不上时先自动重开一轮登录——人手点「重新登录」就能好,那就别让人去点。
// 闸门必须放 localStorage:「这个页签的登录记录丢了」本身就是候选原因之一,
// 闸门放 sessionStorage 会跟着一起丢,自动重登就变成无限跳转。
// 写完立刻读回来确认,写不进去(隐私模式、存储被禁)就不自动。
const AUTO_RELOGIN_KEY = "isestudio_auto_relogin_at"
const AUTO_RELOGIN_COOLDOWN_MS = 60_000

function claimAutoRelogin(now: number): boolean {
  try {
    const last = Number(localStorage.getItem(AUTO_RELOGIN_KEY) ?? "0")
    // 时间戳落在未来只可能是系统时钟被往回拨过,不能让它把自动重登永久卡死:
    // 当作可以重登,下一行随即把它覆盖成当前时刻,闸门自己就恢复了。
    if (Number.isFinite(last) && now - last >= 0 && now - last < AUTO_RELOGIN_COOLDOWN_MS) return false
    const stamp = String(now)
    localStorage.setItem(AUTO_RELOGIN_KEY, stamp)
    return localStorage.getItem(AUTO_RELOGIN_KEY) === stamp
  } catch {
    return false
  }
}

// redirect_uri 固定首页:Keycloak 那边是白名单,逐条登记每个资源页不现实。
// 深链靠 returnTo 自己接回来——否则贴给同事的资源页一登录就退化成首页。
const redirectUri = () => `${location.origin}/`

// 跳转已经发出,调用方不该再往下走。返回一个永不 settle 的 Promise 把后续逻辑挂住,
// 比 throw 干净:不会在控制台留下一堆「未捕获异常」的噪音。
const halt = <T,>(): Promise<T> => new Promise<T>(() => {})

function clear(): void {
  for (const k of Object.values(K)) sessionStorage.removeItem(k)
}

async function exchange(params: Record<string, string>): Promise<void> {
  const r = await fetch(TOKEN_ENDPOINT, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ client_id: CLIENT_ID, ...params }),
  })
  if (!r.ok) throw new Error(`换 token 失败 ${r.status}: ${await r.text()}`)
  const j = await r.json()
  sessionStorage.setItem(K.access, j.access_token)
  sessionStorage.setItem(K.exp, String(Date.now() + (j.expires_in ?? 300) * 1000))
  if (j.refresh_token) sessionStorage.setItem(K.refresh, j.refresh_token)
  if (j.id_token) sessionStorage.setItem(K.id, j.id_token)
}

export function login(): void {
  const state = randomState()
  removeExpiredTransactions(Date.now())
  sessionStorage.setItem(transactionKey(state), JSON.stringify({
    returnTo: location.pathname,
    createdAt: Date.now(),
    exchangeStarted: false,
  } satisfies LoginTransaction))
  location.assign(buildAuthUrl(redirectUri(), state))
}

/** 回调无法匹配当前页签交易时,清令牌并明确发起一轮新的登录;不能重载原回调 URL。 */
export function restartLogin(): void {
  clear()
  removeExpiredTransactions(Date.now())
  login()
}

export function logout(): void {
  const idToken = sessionStorage.getItem(K.id) ?? ""
  clear()
  location.assign(buildLogoutUrl(redirectUri(), idToken))
}

/** 当前是否持有 access token(决定 logout 走 SSO 还是本地路径)。 */
export function hasSession(): boolean {
  return Boolean(sessionStorage.getItem(K.access))
}

/** 401 后清干净 token,让下一次请求回到未登录态。 */
export function clearTokens(): void {
  clear()
}

/** 启动时调用:处理回调 / 确认已登录 / 否则跳登录。返回后即可认为持有有效身份。 */
export function ensureAuthenticated(): Promise<void> {
  let callback: { code: string; state: string } | null
  try {
    // 身份服务拒绝授权时 parseCallback 会抛。本函数不是 async,同步抛会整个越过
    // 调用方挂的 .catch —— 人看到的是白屏而不是能点「重新登录」的失败页。
    // 必须转成 rejected。
    callback = parseCallback(location.search)
  } catch (e) {
    return Promise.reject(e)
  }
  if (callback) {
    const cb = callback
    const existing = inFlightCallbackExchanges.get(cb.state)
    if (existing) return existing

    const now = Date.now()
    const lookup: CallbackCheck = cb.state
      ? lookupTransaction(cb.state, now)
      : { ok: false, reason: "no_state" }
    if (!lookup.ok) {
      console.warn("[sso] 登录回调对不上", {
        reason: lookup.reason,
        callbackState: cb.state.slice(0, 8),
        transactions: transactionDigest(now),
      })
      // 对不上的授权码到此为止:不换票、不放行。自动重登只是替人点了一下
      // 「重新登录」——它会另起一笔登录、另生成一个校验码,照样逐条校验,
      // 防跨站伪造的门一点没松。
      if (claimAutoRelogin(now)) {
        restartLogin()
        return halt<void>()
      }
      return Promise.reject(new Error(lookup.reason))
    }
    const loginTransaction = lookup.transaction

    let exchangePromise!: Promise<void>
    exchangePromise = (async () => {
      sessionStorage.setItem(transactionKey(cb.state), JSON.stringify({
        ...loginTransaction,
        exchangeStarted: true,
      } satisfies LoginTransaction))
      await exchange({ grant_type: "authorization_code", code: cb.code, redirect_uri: redirectUri() })
      sessionStorage.removeItem(transactionKey(cb.state))
      history.replaceState(null, "", loginTransaction.returnTo || location.pathname)
    })().finally(() => {
      if (inFlightCallbackExchanges.get(cb.state) === exchangePromise) {
        inFlightCallbackExchanges.delete(cb.state)
      }
    })
    inFlightCallbackExchanges.set(cb.state, exchangePromise)
    return exchangePromise
  }
  if (sessionStorage.getItem(K.access)) return Promise.resolve()
  login()
  return halt<void>()
}

/**
 * 有效 access_token;将过期则先用 refresh_token 静默续期。
 * 续不上(会话过期/被登出)返回 null——重登由调用方(api.ts 的 401 链)触发,
 * 不在每个请求路径上直接跳转。
 */
export async function getAccessToken(): Promise<string | null> {
  const at = sessionStorage.getItem(K.access)
  const exp = Number(sessionStorage.getItem(K.exp) ?? 0)
  if (at && !needsRefresh(exp, Date.now())) return at

  const rt = sessionStorage.getItem(K.refresh)
  if (rt) {
    try {
      await exchange({ grant_type: "refresh_token", refresh_token: rt })
      return sessionStorage.getItem(K.access)
    } catch {
      // refresh 也失效,往下走:清空,返回 null
    }
  }
  clear()
  return null
}
```

- [ ] **Step 2: 编译 + lint 验证**

Run: `cd frontend && pnpm lint && pnpm build`
Expected: 0 error(新文件无消费方时 tsc 不报 unused——`noUnusedLocals` 只对局部变量,顶层导出不触发)

- [ ] **Step 3: Commit**

```bash
git add frontend/src/lib/sso/auth.ts
git commit -m "feat(sso): OIDC state machine — code exchange, silent refresh, relogin gate

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: api.ts Bearer 注入

**Files:**
- Modify: `frontend/src/lib/api.ts`(request() + 两处裸 fetch)

**Interfaces:**
- Consumes: Task 1 `ssoEnabled`;Task 2 `getAccessToken`。
- Produces: 无新导出;现有 `api.*` 行为不变(SSO 未配置时逐字节不变)。

- [ ] **Step 1: 加 helper + 改造 request()**

在 [api.ts:77](frontend/src/lib/api.ts#L77) 的 import 块里加:

```ts
import { ssoEnabled } from "@/lib/sso/authModel"
import { getAccessToken } from "@/lib/sso/auth"
```

在 `setUnauthorizedHandler`(L81-84)之后加 helper:

```ts
// SSO 启用时给请求注入 Bearer token;否则 undefined,保持现有 cookie 行为。
// 拿不到 token(未登录/refresh 已失效)返回 undefined —— 不带 header 发出,
// 由后端 401 触发 onUnauthorized 链,AuthProvider 的 handler 接 SSO 重登。
async function ssoAuthHeaders(): Promise<HeadersInit | undefined> {
  if (!ssoEnabled()) return undefined
  const token = await getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : undefined
}
```

把 [api.ts:107-123](frontend/src/lib/api.ts#L107-L123) 的 `request` 开头改为:

```ts
async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers)
  const ssoHeaders = await ssoAuthHeaders()
  if (ssoHeaders) {
    for (const [k, v] of Object.entries(ssoHeaders)) headers.set(k, v)
  }
  const res = await fetch(path, { credentials: "include", ...init, headers })
  if (!res.ok) {
    if (res.status === 401 && onUnauthorized) onUnauthorized()
    let detail: unknown = res.statusText
    try {
      const body = await res.json()
      detail = body.detail ?? body
    } catch {
      /* ignore */
    }
    throw new ApiError(res.status, detail)
  }
  // Some endpoints (logout) return trivial JSON; a 204 would have no body.
  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}
```

- [ ] **Step 2: 两处裸 fetch 同样注入**

[api.ts:318](frontend/src/lib/api.ts#L318)(exportOntology)与 [api.ts:440](frontend/src/lib/api.ts#L440)(exportVocabulary)是绕过 `request()` 的裸 fetch——SSO 用户没有 cookie,不注入的话这两处永远 401。分别改为:

```ts
  exportOntology: async (ksId: string, fmt: string): Promise<string> => {
    const res = await fetch(`/api/knowledge/${ksId}/ontology/export?fmt=${fmt}`, {
      credentials: "include",
      headers: await ssoAuthHeaders(),
    })
    if (!res.ok) {
      if (res.status === 401 && onUnauthorized) onUnauthorized()
      throw new Error(`${res.status}: ${res.statusText}`)
    }
    return res.text()
  },
```

```ts
  exportVocabulary: async (ksId: string, fmt = "turtle"): Promise<string> => {
    const res = await fetch(`/api/knowledge/${ksId}/vocabulary/export?fmt=${fmt}`, {
      credentials: "include",
      headers: await ssoAuthHeaders(),
    })
    if (!res.ok) throw new Error(`${res.status}: ${res.statusText}`)
    return res.text()
  },
```

- [ ] **Step 3: 编译 + lint + 单测验证**

Run: `cd frontend && pnpm test && pnpm lint && pnpm build`
Expected: 11 passed + 0 error。

- [ ] **Step 4: Commit**

```bash
git add frontend/src/lib/api.ts
git commit -m "feat(sso): inject Bearer token into all API requests

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: AuthProvider + LoginPage + i18n

**Files:**
- Modify: `frontend/src/lib/auth.tsx`(AuthProvider 接线)
- Modify: `frontend/src/pages/LoginPage.tsx`(SSO 按钮 + ssoError 展示)
- Modify: `frontend/src/lib/i18n.tsx`(6 新键 × 2 语言)

**Interfaces:**
- Consumes: Task 1 `ssoEnabled`;Task 2 `login` / `logout` / `hasSession` / `clearTokens` / `ensureAuthenticated`。
- Produces: `AuthState` 加 `ssoError: string | null` 与 `clearSsoError(): void`。

- [ ] **Step 1: 改 AuthProvider**

把 [auth.tsx](frontend/src/lib/auth.tsx) 全文替换为:

```tsx
import { createContext, useCallback, useContext, useEffect, useState } from "react"
import type { ReactNode } from "react"
import { api, setUnauthorizedHandler } from "@/lib/api"
import type { User } from "@/lib/types"
import { ssoEnabled } from "@/lib/sso/authModel"
import {
  clearTokens, ensureAuthenticated, hasSession, login as ssoLogin, logout as ssoLogout,
} from "@/lib/sso/auth"

interface AuthState {
  user: User | null
  loading: boolean
  /** SSO 回调校验失败的 reason code(no_state / unknown_transaction / expired / already_exchanged)。 */
  ssoError: string | null
  login: (username: string, password: string) => Promise<void>
  logout: () => Promise<void>
  refresh: () => Promise<void>
  clearSsoError: () => void
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null)
  const [loading, setLoading] = useState(true)
  const [ssoError, setSsoError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    try {
      setUser(await api.me())
    } catch {
      setUser(null)
    }
  }, [])

  useEffect(() => {
    // Any 401 from a background call (expired session) drops us to the login screen.
    // SSO 模式下清掉 token 后自动回 Keycloak——重登大概率换到新 token;若回调
    // 又对不上,claimAutoRelogin 的 60s 闸会挡住,不会无限跳转。
    setUnauthorizedHandler(() => {
      if (ssoEnabled()) {
        clearTokens()
        ssoLogin()
      } else {
        setUser(null)
      }
    })
    const boot = async () => {
      try {
        if (ssoEnabled()) {
          // 处理回调换票 / 确认有 token / 否则跳 Keycloak。
          await ensureAuthenticated()
          setSsoError(null)
        }
        await refresh()
      } catch (err) {
        // 回调对不上(解析失败 / 校验失败)且自动重登闸拒绝时落这里:
        // 把 reason code 摆到登录页,由人自己点。
        if (err instanceof Error) setSsoError(err.message)
      } finally {
        setLoading(false)
      }
    }
    boot()
    return () => setUnauthorizedHandler(null)
  }, [refresh])

  const login = useCallback(async (username: string, password: string) => {
    const u = await api.login(username, password)
    setUser(u)
  }, [])

  const logout = useCallback(async () => {
    try {
      // SSO 会话跳 Keycloak 单点登出(id_token_hint);本地账号走现有 cookie 登出。
      if (ssoEnabled() && hasSession()) {
        ssoLogout() // location.assign,不会返回
      } else {
        await api.logout()
      }
    } finally {
      setUser(null)
    }
  }, [])

  const clearSsoError = useCallback(() => setSsoError(null), [])

  return (
    <AuthContext.Provider value={{ user, loading, ssoError, login, logout, refresh, clearSsoError }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error("useAuth must be used within AuthProvider")
  return ctx
}
```

- [ ] **Step 2: 改 LoginPage**

在 [LoginPage.tsx:2](frontend/src/pages/LoginPage.tsx#L2) 的 import 区加:

```tsx
import { ssoEnabled } from "@/lib/sso/authModel"
import { login as ssoLogin } from "@/lib/sso/auth"
import { useI18n, type MessageKey } from "@/lib/i18n"
```

(第 4 行原有的 `import { useI18n } from "@/lib/i18n"` 合并删除,避免重复 import。)

`const { login } = useAuth()` 改为 `const { login, ssoError, clearSsoError } = useAuth()`,并在 `submit` 函数后加:

```tsx
  const ssoAvailable = ssoEnabled()
```

在 [LoginPage.tsx:61-64](frontend/src/pages/LoginPage.tsx#L61-L64) 的错误行与提交按钮之间插入 SSO 区块,整段替换为:

```tsx
            {error && <p className="text-sm text-destructive">{error}</p>}
            {ssoError && (
              <p className="text-sm text-destructive">{t(("sso.error." + ssoError) as MessageKey)}</p>
            )}
            <Button type="submit" className="w-full" disabled={busy || !username.trim() || !password}>
              {busy && <Loader2 className="h-4 w-4 animate-spin" />} {t("login.submit")}
            </Button>
            {ssoAvailable && (
              <>
                <div className="relative">
                  <div className="absolute inset-0 flex items-center">
                    <span className="w-full border-t" />
                  </div>
                  <div className="relative flex justify-center text-xs uppercase">
                    <span className="bg-card px-2 text-muted-foreground">{t("login.or")}</span>
                  </div>
                </div>
                <Button
                  type="button"
                  variant="outline"
                  className="w-full"
                  onClick={() => {
                    clearSsoError()
                    ssoLogin()
                  }}
                >
                  {t("login.ssoButton")}
                </Button>
              </>
            )}
```

- [ ] **Step 3: i18n 新键**

在 [i18n.tsx:698](frontend/src/lib/i18n.tsx#L698)(en 对象 `"login.failed": "Sign-in failed",` 之后)加:

```ts
  "login.ssoButton": "Continue with SSO",
  "login.or": "or",
  "sso.error.no_state": "The login callback is missing its verification code. Please try again.",
  "sso.error.unknown_transaction": "No login record was found in this tab. Please try again.",
  "sso.error.expired": "The login was not completed within 15 minutes. Please try again.",
  "sso.error.already_exchanged": "This login has already been processed. Please try again.",
```

在 zh 对象 `"login.failed": ...` 镜像位置加(用 Grep 定位 `"login.failed"` 在 zh 区的行):

```ts
  "login.ssoButton": "使用 SSO 登录",
  "login.or": "或",
  "sso.error.no_state": "登录返回的地址缺少校验码，请重新登录",
  "sso.error.unknown_transaction": "这个标签页里找不到本次登录的记录，请重新登录",
  "sso.error.expired": "本次登录超过 15 分钟没有完成，请重新登录",
  "sso.error.already_exchanged": "本次登录已经处理过一次，请重新登录",
```

- [ ] **Step 4: 编译验证(tsc 会强制 zh 镜像键齐全)**

Run: `cd frontend && pnpm test && pnpm lint && pnpm build`
Expected: 11 passed + 0 error。若漏了 zh 键,`pnpm build` 报 `Record<MessageKey, string>` 缺键——补上再跑。

- [ ] **Step 5: Commit**

```bash
git add frontend/src/lib/auth.tsx frontend/src/pages/LoginPage.tsx frontend/src/lib/i18n.tsx
git commit -m "feat(sso): AuthProvider boot/logout wiring + login page SSO button + i18n

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: 全量回归(SSO 禁用路径逐字节不变)

**Files:** 无。

- [ ] **Step 1: 单测 + lint + build**

Run: `cd frontend && pnpm test && pnpm lint && pnpm build`
Expected: 11 passed + 0 error + 0 warning

- [ ] **Step 2: e2e 冒烟(本地登录路径——SSO 未配置时走原 cookie 登录)**

前置:后端按 e2e 文档起好(`test:e2e:dotnet` 项目自带)。Run: `cd frontend && pnpm test:e2e:dotnet`
Expected: 现有 spec(session.spec 等)全绿——SSO 未配置时 `ssoEnabled()` false,`ssoAuthHeaders()` 返回 undefined,登录页无 SSO 按钮,行为不变。

- [ ] **Step 3: 手工冒烟(可选,有 Keycloak 环境时)**

`VITE_AUTH_AUTHORITY` / `VITE_AUTH_CLIENT_ID` 指到 Keycloak 后 `pnpm dev`:登录页出现「Continue with SSO / 使用 SSO 登录」按钮 → 点击 → Keycloak 登录 → 回调回首页 → Network 面板确认 API 请求带 `Authorization: Bearer` → 退出登录回 Keycloak logout。

- [ ] **Step 4: Commit 收尾(若有改动)**

```bash
git status --short
# 干净则跳过
```

---

## Self-Review

**Spec 覆盖**:§5.1 三级配置 → Task 1(注入 > VITE_ > 未配置,三测试锁定);§5.2 authModel 六函数 → Task 1 全实现 + 11 测试;§5.3 状态机(state 事务/15min TTL/冷却闸/refresh 续期/logout id_token_hint/去 X-Organization 与 keycloakReachable)→ Task 2(与 goodcrew 逐段对照,org 相关全删,错误文案改为 reason code + i18n 翻译);§5.4 四个文件改造表 → Task 3(api.ts 含两处裸 fetch)+ Task 4(auth.tsx / LoginPage / i18n;main.tsx 按 spec 不改);§5.5 401 语义 → Task 3 保留现有 onUnauthorized 链 + Task 4 handler SSO 分支;§6.2 前端测试 → Task 1(11 例,vitest 新引入)。§7 部署 → 独立计划(deploy)。

**缺口**:spec §6.2 说"照搬 goodcrew 现有 test"——goodcrew 测试含 org scope 断言,ISEStudio 版 scope 为 `openid profile email`,测试已相应改写并显式断言 `code_challenge` 不存在(D7 锁定)。spec §5.3 未提到错误文案语言,计划决策:抛 reason code,UI 用 i18n 双语翻译(与 ISEStudio 现有双语 UI 一致,优于 goodcrew 的硬编码中文)。

**类型一致性**:`getAccessToken(): Promise<string | null>`(Task 2 定义,Task 3 消费);`ssoError: string | null` + `clearSsoError()`(Task 4 定义并消费);`CallbackRejection` 四个 code 与 i18n 键 `sso.error.*` 一一对应(Task 2 定义,Task 4 翻译)。

**执行顺序依赖**:Task 2 ← Task 1;Task 3 ← Task 1+2;Task 4 ← Task 1+2;Task 5 全量。Task 3/4 可并行(不同文件)。
