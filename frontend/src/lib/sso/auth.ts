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
  // 无回调、无 token:停在登录页——本地表单与 SSO 按钮并存(决策 D1,
  // spec §3 架构图)。跳 Keycloak 只在用户点按钮(login())、会话过期
  // (401 链 ssoLogin)或回调对不上自动重登(restartLogin)时发生;
  // 启动时静默抢跳会让本地账号无路可走。
  return Promise.resolve()
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
