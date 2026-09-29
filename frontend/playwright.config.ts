import { defineConfig, devices } from "@playwright/test"

/**
 * Stage 5 .NET end-to-end suite.
 *
 * The config wires the three specs already shipped under
 * `frontend/e2e/dotnet/` (upload-extract-publish, vocabulary, session)
 * to a project named `dot-net` so the Playwright test runner can find
 * them. The suite is intentionally Chromium-only — the Stage 5 brief
 * scopes the smoke matrix to one browser to keep the gate cheap.
 *
 * Browser pages are served by Vite; its `/api` and `/mcp` proxy target
 * is `DOTNET_BASE_URL`. If that variable is absent, Playwright boots
 * the .NET API on the pinned port. `DOTNET_BASE_URL` can point to an
 * already-running isolated test backend when needed.
 *
 * `e2e/dotnet/helpers/config.ts` uses the same backend URL for its
 * health probe, while Playwright's `baseURL` remains the Vite origin.
 */

const DOTNET_PORT = Number(process.env.DOTNET_E2E_PORT ?? 18080)
const DOTNET_BASE_URL =
  process.env.DOTNET_BASE_URL ?? `http://localhost:${DOTNET_PORT}`
const FRONTEND_PORT = Number(process.env.FRONTEND_E2E_PORT ?? 5173)
const FRONTEND_BASE_URL = `http://127.0.0.1:${FRONTEND_PORT}`

const webServers = [
  ...(process.env.DOTNET_BASE_URL ? [] : [{
    command: "dotnet run --no-launch-profile --project ../src/ISEStudio --urls=http://+:" + DOTNET_PORT,
    url: `${DOTNET_BASE_URL}/api/health`,
    reuseExistingServer: true,
    timeout: 120_000,
    stdout: "pipe" as const,
    stderr: "pipe" as const,
  }]),
  {
    command: `pnpm dev --host 127.0.0.1 --port ${FRONTEND_PORT} --strictPort`,
    url: FRONTEND_BASE_URL,
    reuseExistingServer: false,
    timeout: 120_000,
    stdout: "pipe" as const,
    stderr: "pipe" as const,
    env: { VITE_BACKEND_PROXY_TARGET: DOTNET_BASE_URL },
  },
]

export default defineConfig({
  testDir: "e2e",
  testMatch: /e2e\/dotnet\/.*\.spec\.ts$/,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false, // the .NET suite shares a single seeded backend
  workers: 1,
  reporter: process.env.CI
    ? [["github"], ["list"]]
    : [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: FRONTEND_BASE_URL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "dot-net", use: { ...devices["Desktop Chrome"] } }],
  webServer: webServers,
})