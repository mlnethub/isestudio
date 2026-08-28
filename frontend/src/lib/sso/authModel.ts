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
  // 剩余时间 ≤ 60s 就换:边界上发出的请求不能带着将死的 token。
  return expiresAtMs - nowMs <= REFRESH_SKEW_MS
}

/** state 用 getRandomValues —— 它在 http 非安全上下文下也可用,不像 crypto.subtle。 */
export function randomState(): string {
  const b = new Uint8Array(16)
  crypto.getRandomValues(b)
  return Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("")
}
