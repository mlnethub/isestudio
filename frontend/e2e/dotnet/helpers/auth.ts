import { expect, type Page } from "@playwright/test"

/**
 * Auth helpers for E2E specs targeting the .NET backend.
 *
 * These helpers intentionally avoid touching anything under `frontend/src/`.
 * They drive the deployed UI as a user would, and they fail informatively
 * with a clear message when the backend is unreachable so the CI job fails
 * fast instead of hanging on a 30-second Playwright timeout.
 */

const DEFAULT_USERNAME = process.env.E2E_ADMIN_USERNAME ?? "admin"
const DEFAULT_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? "admin"

/**
 * Logs in as the seeded admin against whatever backend the Playwright
 * `baseURL` is pointing at (the .NET port when the spec is invoked via
 * `pnpm --dir frontend exec playwright test e2e/dotnet --grep dotnet`).
 *
 * The login form lives at `/login` and exposes `Username` / `Password`
 * fields plus a `Sign in` submit button.
 */
export async function loginAsAdmin(
  page: Page,
  options: { username?: string; password?: string } = {},
): Promise<void> {
  const username = options.username ?? DEFAULT_USERNAME
  const password = options.password ?? DEFAULT_PASSWORD

  await page.goto("/login", { waitUntil: "domcontentloaded" })

  // Pre-flight: confirm the page rendered. If the .NET backend is offline,
  // Vite's proxy will return an error page and the form will not exist.
  await expect(
    page.getByRole("heading", { name: /sign in|log in/i }),
    `Login page did not render — is the .NET backend reachable at ${page.url()}?`,
  ).toBeVisible({ timeout: 10_000 })

  await page.getByLabel(/username/i).fill(username)
  await page.getByLabel(/password/i).fill(password)
  await page.getByRole("button", { name: /sign in|log in|submit/i }).click()

  // The shell renders the "Documents" link in the side nav once the
  // session cookie is set, so the post-login landing page exposes it.
  await expect(
    page.getByRole("link", { name: /documents/i }).first(),
    "Login did not complete — the side-nav 'Documents' link never appeared.",
  ).toBeVisible({ timeout: 15_000 })
}

/**
 * Logs out by clearing the auth context. The frontend relies on
 * `useAuth().logout()`; we emulate the same effect by deleting cookies
 * (the session cookie is `session_token`).
 */
export async function logoutAsAdmin(page: Page): Promise<void> {
  await page.context().clearCookies()
}