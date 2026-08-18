/**
 * Backend URL helpers for the .NET E2E suite.
 *
 * Playwright's `baseURL` is shared with the existing frontend specs;
 * the .NET specs need to know whether the .NET backend (port 18080 by
 * default for Stage 5) is reachable, because the upstream Vite dev
 * server proxies unknown paths to whatever backend the spec sets.
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