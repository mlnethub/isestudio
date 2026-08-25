import { expect, test } from "@playwright/test"

import { loginAsAdmin } from "./helpers/auth"
import { isDotNetBackendReachable } from "./helpers/config"
import { publishCurrentDraft } from "./helpers/publish"

/**
 * .NET end-to-end coverage for the canonical ISEStudio workflow:
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
    // Enter the seeded knowledge system before reaching the
    // Documents & Extraction side-nav link.
    await page.getByRole("link", { name: /open/i }).first().click()
    await page.getByRole("link", { name: /documents/i }).click()
    // The Documents panel exposes a hidden <input type="file"> opened
    // by an "Upload" button. Drive the input directly — it's the same
    // primitive the trigger uses, just without the click intermediary.
    await page.locator('input[type="file"]').first().setInputFiles("e2e/fixtures/pump.pdf")
    // Wait for the upload to land: the row for `pump.pdf` must appear in
    // the table. Upload itself only flips parse_status to "pending";
    // parse is a separate step on the next button click.
    const pdfRow = page.getByRole("row").filter({ hasText: /pump\.pdf/i }).first()
    await expect(pdfRow, "Uploaded pump.pdf row never appeared in the documents table.")
      .toBeVisible({ timeout: 15_000 })
    // Step 1 — parse the PDF (per-row "Parse" button flips status to
    // "parsed" / "已解析"). The Extract button only renders once parse
    // succeeds, so this is a precondition for the next step.
    await pdfRow.getByRole("button", { name: /^parse$|reparse|解析/i }).first().click()
    await expect(pdfRow.getByText(/parsed|已解析/i).first(),
      "pump.pdf did not reach 'parsed' state — backend PDF parser may be unhealthy.",
    ).toBeVisible({ timeout: 60_000 })
    // Step 2 — open the per-row Extract dialog and confirm. We click
    // the dialog's primary action button rather than the row button, so
    // the same selector works regardless of how the dialog labels itself
    // across locales.
    await pdfRow.getByRole("button", { name: /extract|抽取/i }).first().click()
    await page.getByRole("button", { name: /extract|抽取|start|开始/i }).last().click()
    await expect(page.getByText("completed")).toBeVisible({ timeout: 60_000 })
    await publishCurrentDraft(page)
    await expect(page.getByText("published")).toBeVisible()
  })
})