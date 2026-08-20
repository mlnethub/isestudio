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
 * Backend bring-up is delegated to the `webServer` block below: if
 * `DOTNET_BASE_URL` is already in the environment (CI pre-started the
 * backend, or a developer is pointing at a remote instance), the
 * config leaves the server alone. Otherwise Playwright boots
 * `dotnet run` against `src/OnToPilot` on the pinned port and waits
 * for `/api/health` to respond before letting specs run.
 *
 * `e2e/dotnet/helpers/config.ts` already reads `DOTNET_BASE_URL` (or
 * falls back to `http://localhost:18080`), so the same env-var knob
 * controls both the runner and the in-spec health probe — the spec
 * authors do not need to know whether Playwright booted the server
 * or whether it was already up.
 */

const DOTNET_PORT = Number(process.env.DOTNET_E2E_PORT ?? 18080)
const DOTNET_BASE_URL =
  process.env.DOTNET_BASE_URL ?? `http://localhost:${DOTNET_PORT}`

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
    baseURL: DOTNET_BASE_URL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "dot-net", use: { ...devices["Desktop Chrome"] } }],
  webServer: process.env.DOTNET_BASE_URL
    ? undefined
    : {
        command:
          "dotnet run --project ../src/OnToPilot --urls=http://+:" +
          DOTNET_PORT,
        url: `${DOTNET_BASE_URL}/api/health`,
        reuseExistingServer: true,
        timeout: 120_000,
        stdout: "pipe",
        stderr: "pipe",
      },
})