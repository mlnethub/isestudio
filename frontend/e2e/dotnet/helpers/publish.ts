import { expect, type Page } from "@playwright/test"

/**
 * Workflow helpers for the upload → extract → publish E2E flow.
 *
 * Mirrors the contract the ISEStudio .NET controller layer exposes:
 *   POST /api/knowledge/{ks_id}/documents/upload
 *   POST /api/knowledge/{ks_id}/documents/{id}/parse
 *   POST /api/extraction/run        (extraction queue)
 *   POST /api/releases              (publish current draft)
 *
 * The frontend's review panel gates the publish button behind a
 * "Review & Publish" affordance; the helper clicks through whichever
 * label the UI happens to expose in the current locale.
 */

const PUBLISH_BUTTON_LABELS = [
  /review\s*&\s*publish/i,
  /review and publish/i,
  /^publish$/i,
  /publish current draft/i,
]

/**
 * Clicks whatever the UI calls the "publish current draft" action,
 * then waits for the release status to flip to `published`. Times out
 * quickly on purpose so the spec fails informatively when the .NET
 * backend is offline.
 */
export async function publishCurrentDraft(page: Page): Promise<void> {
  let clicked = false
  for (const label of PUBLISH_BUTTON_LABELS) {
    const button = page.getByRole("button", { name: label })
    if ((await button.count()) > 0) {
      await button.first().click()
      clicked = true
      break
    }
  }

  if (!clicked) {
    throw new Error(
      "publishCurrentDraft: no publish button found. " +
        "Expected one of: " +
        PUBLISH_BUTTON_LABELS.map((r) => r.source).join(", "),
    )
  }

  await expect(
    page.getByText(/published/i).first(),
    "Release did not reach the 'published' state within the timeout.",
  ).toBeVisible({ timeout: 15_000 })
}