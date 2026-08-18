import { expect, test } from "@playwright/test"

import { loginAsAdmin, logoutAsAdmin } from "./helpers/auth"
import { isDotNetBackendReachable } from "./helpers/config"

/**
 * .NET end-to-end coverage for the session round-trip:
 *   - log in as the seeded admin
 *   - assert the session-protected side nav becomes visible
 *   - log out (clear session cookie)
 *   - assert the login form is presented again on next navigation
 *
 * The .NET auth surface is `AuthController` (see `src/OnToPilot/Controllers/AuthController.cs`).
 */

test.describe("dotnet / session round-trip", () => {
  test.beforeAll(async () => {
    const reachable = await isDotNetBackendReachable()
    test.skip(
      !reachable,
      ".NET backend is not reachable on /api/health. " +
        "Start it (or set DOTNET_BASE_URL) and rerun this spec.",
    )
  })

  test("login then logout against dotnet", async ({ page }) => {
    await loginAsAdmin(page)

    // The Documents link only renders when the auth context is established.
    await expect(
      page.getByRole("link", { name: /documents/i }).first(),
      "Post-login Documents link never appeared — session was not established.",
    ).toBeVisible({ timeout: 10_000 })

    await logoutAsAdmin(page)

    // After clearing the cookie, navigating to a protected route must
    // bounce back to the login page.
    await page.goto("/")
    await expect(
      page.getByLabel(/password/i),
      "Login form did not reappear after logout — the session cookie may not have been cleared.",
    ).toBeVisible({ timeout: 10_000 })
  })
})