import { expect, type Page } from "@playwright/test"

/**
 * Creates a release draft, approves it, and publishes it through the
 * Releases UI. Capture runs asynchronously before approval is available.
 */
export async function publishCurrentDraft(page: Page): Promise<void> {
  const main = page.getByRole("main")
  const draftResponsePromise = page.waitForResponse((response) =>
    response.request().method() === "POST"
      && /^\/api\/knowledge\/[^/]+\/releases$/.test(new URL(response.url()).pathname),
  )
  await main.getByRole("button", { name: /create draft|创建草稿/i }).click()
  const draftResponse = await draftResponsePromise
  expect(draftResponse.ok()).toBeTruthy()
  const draft = await draftResponse.json() as { id: string }
  const releasePath = new URL(draftResponse.url()).pathname + "/" + draft.id

  const approveButton = main.getByRole("button", { name: /approve|审核通过/i })
  await expect(approveButton, "Release snapshot did not become ready for approval.")
    .toBeVisible({ timeout: 90_000 })
  await approveButton.click()

  const publishButton = main.getByRole("button", { name: /^publish$|^发布$/i })
  await expect(publishButton, "Approved release did not expose its publish action.")
    .toBeVisible({ timeout: 15_000 })
  const publishResponsePromise = page.waitForResponse((response) =>
    response.request().method() === "POST"
      && new URL(response.url()).pathname === releasePath + "/publish",
  )
  await publishButton.click()
  const publishResponse = await publishResponsePromise
  expect(publishResponse.ok()).toBeTruthy()
  const published = await publishResponse.json() as { id: string; status: string }
  expect(published.id).toBe(draft.id)
  expect(published.status, "The newly created release was not published.").toBe("published")

  await expect(main.getByText(/published|已发布/i).first(), "Release was not published.")
    .toBeVisible({ timeout: 30_000 })
}