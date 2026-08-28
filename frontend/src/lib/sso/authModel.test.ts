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
