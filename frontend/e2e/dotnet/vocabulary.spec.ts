import { expect, test } from "@playwright/test"

import { loginAsAdmin } from "./helpers/auth"
import { isDotNetBackendReachable } from "./helpers/config"

/**
 * .NET end-to-end coverage for the SKOS vocabulary panel:
 *   - open the vocabulary tab
 *   - create a concept
 *   - edit the concept
 *   - delete the concept
 *
 * The frontend exposes the vocabulary tab under `/knowledge/{ks_id}`
 * in the `Terminology` panel. The .NET controllers backing it are
 * `VocabularyController` (see `src/OnToPilot/Controllers/VocabularyController.cs`).
 */

test.describe("dotnet / vocabulary (SKOS CRUD)", () => {
  test.beforeAll(async () => {
    const reachable = await isDotNetBackendReachable()
    test.skip(
      !reachable,
      ".NET backend is not reachable on /api/health. " +
        "Start it (or set DOTNET_BASE_URL) and rerun this spec.",
    )
  })

  test("create, edit and delete a vocabulary concept against dotnet", async ({ page }) => {
    await loginAsAdmin(page)

    // The first knowledge system on the seeded backend is the default
    // landing surface. Click whichever Card-as-link tile is rendered.
    await page.getByRole("link", { name: /open/i }).first().click()

    // The side nav exposes the vocabulary section as a "Terminology" link
    // (URL: /knowledge/{ksId}/vocabulary). Main content does not yet use
    // a tablist primitive, so a `getByRole('tab')` selector would miss it.
    await page.getByRole("link", { name: /terminology|vocabulary/i }).click()

    const uniqueLabel = `dotnet-e2e-${Date.now()}`

    // Create a new concept.
    await page.getByRole("button", { name: /add concept|new concept|\+concept/i }).first().click()
    await page.getByLabel(/preferred label|prefLabel|label/i).first().fill(uniqueLabel)
    await page.getByRole("button", { name: /save|create|confirm/i }).click()
    await expect(page.getByText(uniqueLabel), `Concept "${uniqueLabel}" was not rendered after creation.`).toBeVisible({
      timeout: 15_000,
    })

    // Edit it.
    const row = page.getByRole("row").filter({ hasText: uniqueLabel })
    await row.getByRole("button", { name: /edit|pencil/i }).first().click()
    const edited = `${uniqueLabel}-edited`
    await page.getByLabel(/preferred label|prefLabel|label/i).first().fill(edited)
    await page.getByRole("button", { name: /save|update|confirm/i }).click()
    await expect(page.getByText(edited), `Edited concept "${edited}" did not appear.`).toBeVisible({
      timeout: 15_000,
    })

    // Delete it. The frontend wraps destructive actions in a confirm dialog.
    const editedRow = page.getByRole("row").filter({ hasText: edited })
    await editedRow.getByRole("button", { name: /delete|trash/i }).first().click()
    const confirm = page.getByRole("button", { name: /^confirm|yes|delete$/i })
    if ((await confirm.count()) > 0) {
      await confirm.first().click()
    }
    await expect(page.getByText(edited), `Deleted concept "${edited}" is still rendered.`).toHaveCount(0, {
      timeout: 15_000,
    })
  })
})