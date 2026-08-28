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
