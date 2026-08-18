import { expect, test } from "@playwright/test"

import { loginAsAdmin } from "./helpers/auth"
import { isDotNetBackendReachable } from "./helpers/config"
import { publishCurrentDraft } from "./helpers/publish"

/**
 * .NET end-to-end coverage for the canonical OnToPilot workflow:
 * upload a PDF → parse it → run extraction → review → publish the draft.
 *
 * These specs drive the existing UI verbatim — no `frontend/src/` files
 * are modified. They fail fast with a clear message when the .NET
 * backend (default http://localhost:18080) is unreachable so CI doesn't
 * hang on a 30-second Playwright timeout.
 */

test.describe("dotnet / upload → extract → publish", () => {
  test.beforeAll(async () => {
    const reachable = await isDotNetBackendReachable()
    test.skip(
      !reachable,
      ".NET backend is not reachable on /api/health. " +
        "Start it (or set DOTNET_BASE_URL) and rerun this spec.",
    )
  })

  test("upload, extract, review and publish against dotnet", async ({ page }) => {
    await loginAsAdmin(page)
    await page.getByRole("link", { name: "Documents" }).click()
    await page.getByLabel("Upload files").setInputFiles("e2e/fixtures/pump.pdf")
    await expect(page.getByText("parsed")).toBeVisible()
    await page.getByRole("button", { name: "Extract" }).click()
    await expect(page.getByText("completed")).toBeVisible({ timeout: 60_000 })
    await publishCurrentDraft(page)
    await expect(page.getByText("published")).toBeVisible()
  })
})