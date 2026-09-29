/**
 * Backend URL helpers for the .NET E2E suite.
 *
 * Browser pages use Playwright's Vite `baseURL`; this URL is only for
 * probing the .NET backend, which Vite proxies `/api` and `/mcp` to.
 * The default backend port is 18080 for the .NET E2E suite.
 */

export const DOTNET_HEALTH_URL =
  process.env.DOTNET_BASE_URL ?? "http://localhost:18080"

export const DOTNET_MCP_URL = `${DOTNET_HEALTH_URL.replace(/\/$/, "")}/mcp`

/**
 * Returns true when the .NET backend's pinned `/api/health` endpoint
 * answers within the given budget. Used by `beforeAll` hooks so the
 * spec fails fast with a clear message instead of stalling on a 30 s
 * Playwright timeout when the backend isn't running locally.
 */
export async function isDotNetBackendReachable(
  fetchImpl: typeof fetch = fetch,
  timeoutMs = 2_500,
): Promise<boolean> {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  try {
    const res = await fetchImpl(`${DOTNET_HEALTH_URL}/api/health`, {
      signal: controller.signal,
    })
    return res.ok
  } catch {
    return false
  } finally {
    clearTimeout(timer)
  }
}