import { expect, test } from "@playwright/test"

import { loginAsAdmin } from "./helpers/auth"
import { isDotNetBackendReachable } from "./helpers/config"

declare const process: { env: Record<string, string | undefined> }

function usesIsolatedSourceTestDatabase(): boolean {
  return process.env.ISESTUDIO__Persistence__Provider?.toLowerCase() === "sqlite"
    && (process.env.ISESTUDIO__Persistence__SqliteConnection ?? "").includes("ontopilot-sources-e2e-")
}

test.describe("dotnet / ingestion sources", () => {
  test.beforeAll(async () => {
    const reachable = await isDotNetBackendReachable()
    test.skip(
      !reachable,
      ".NET backend is not reachable on /api/health. Start it and rerun this spec.",
    )
  })

  test("shows the folder source separately from virtual folders", async ({ page }) => {
    await loginAsAdmin(page)
    const origin = new URL(page.url()).origin
    const systemsResponse = await page.request.get(`${origin}/api/knowledge`)
    expect(systemsResponse.ok()).toBeTruthy()
    const systems = await systemsResponse.json() as { id: string }[]
    let ksId = systems[0]?.id

    if (!ksId) {
      test.skip(!usesIsolatedSourceTestDatabase(), "Creating a knowledge system requires the isolated SQLite E2E database.")
      const createResponse = await page.request.post(`${origin}/api/knowledge`, {
        data: { name: `Source UI E2E ${Date.now()}`, description: "Temporary isolated UI contract" },
      })
      expect(createResponse.ok()).toBeTruthy()
      ksId = (await createResponse.json() as { id: string }).id
    }

    const sourcesResponse = page.waitForResponse((response) => {
      const url = new URL(response.url())
      return url.pathname === `/api/knowledge/${ksId}/ingestion-sources`
        && response.request().method() === "GET"
    })
    await page.goto(`/knowledge/${ksId}/documents`)
    await page.getByRole("tab", { name: /sources|来源/i }).click()
    const response = await sourcesResponse
    expect(response.ok()).toBeTruthy()
    const sources = await response.json() as {
      kind: string
      supports_push_token: boolean
      name: string
      last_synced_at: string | null
      last_sync_status: string
      created_at: string
    }[]
    const folderSource = sources.find((source) => source.kind === "folder")
    expect(folderSource).toBeTruthy()
    expect(folderSource?.last_synced_at).toBeNull()
    expect(folderSource?.last_sync_status).toBe("never")
    expect(folderSource?.supports_push_token).toBe(false)
    expect(folderSource?.created_at).toBeTruthy()
    await expect(page.getByText(/never synced|尚未同步/i).first()).toBeVisible()
    await expect(page.getByRole("button", { name: /sync now|立即同步/i })).toHaveCount(0)
    await expect(page.getByRole("button", { name: /manage push token|管理推送令牌/i })).toHaveCount(0)

    await page.getByRole("tab", { name: /documents|文档/i }).click()
    await expect(page.getByText(/root|根目录/i)).toBeVisible()
  })

  test("uploads through a second folder source and preserves it when moving virtual folders", async ({ page }) => {
    test.skip(!usesIsolatedSourceTestDatabase(), "Mutating Source and document contracts require the isolated SQLite E2E database.")

    await loginAsAdmin(page)
    const origin = new URL(page.url()).origin
    const createKsResponse = await page.request.post(`${origin}/api/knowledge`, {
      data: { name: `Source Upload E2E ${Date.now()}`, description: "Temporary isolated upload contract" },
    })
    expect(createKsResponse.ok()).toBeTruthy()
    const ksId = (await createKsResponse.json() as { id: string }).id
    const sourceName = `Upload source ${Date.now()}`

    await page.goto(`/knowledge/${ksId}/documents`)
    await page.getByRole("tab", { name: /sources|来源/i }).click()
    await page.getByRole("button", { name: /add source/i }).click()
    const sourceDialog = page.getByRole("dialog")
    await sourceDialog.getByRole("combobox").click()
    await expect(page.getByRole("option", { name: "folder", exact: true })).toBeVisible()
    await expect(page.getByRole("option", { name: "azure_blob", exact: true })).toHaveCount(0)
    await page.getByRole("option", { name: "folder", exact: true }).click()

    const unsupportedKindResponse = await page.request.post(`${origin}/api/knowledge/${ksId}/ingestion-sources`, {
      data: { kind: "azure_blob", name: `Unsupported ${Date.now()}` },
    })
    expect(unsupportedKindResponse.status()).toBe(400)

    await sourceDialog.getByLabel(/name/i).fill(sourceName)
    const sourceResponsePromise = page.waitForResponse((response) => {
      const url = new URL(response.url())
      return url.pathname === `/api/knowledge/${ksId}/ingestion-sources`
        && response.request().method() === "POST"
    })
    await sourceDialog.getByRole("button", { name: /save/i }).click()
    const sourceResponse = await sourceResponsePromise
    expect(sourceResponse.ok()).toBeTruthy()
    const source = await sourceResponse.json() as { id: string; kind: string }
    expect(source.kind).toBe("folder")
    const emptySourceName = `Empty filter source ${Date.now()}`
    const emptySourceResponse = await page.request.post(`${origin}/api/knowledge/${ksId}/ingestion-sources`, {
      data: { kind: "folder", name: emptySourceName },
    })
    expect(emptySourceResponse.ok()).toBeTruthy()
    const emptySource = await emptySourceResponse.json() as { id: string }

    await page.getByRole("tab", { name: /documents|文档/i }).click()
    await page.getByLabel(/upload source/i).click()
    await page.getByRole("option", { name: sourceName, exact: true }).click()

    const uploadFolder = `/source-ui-${Date.now()}`
    page.once("dialog", (dialog) => dialog.accept(uploadFolder.slice(1)))
    await page.getByRole("button", { name: /new folder/i }).click()
    await expect(page.getByRole("button", { name: uploadFolder.slice(1), exact: true })).toBeVisible()

    const uploadResponsePromise = page.waitForResponse((response) => {
      const url = new URL(response.url())
      return url.pathname === `/api/knowledge/${ksId}/documents/upload`
        && response.request().method() === "POST"
    })
    await page.locator('input[type="file"]').first().setInputFiles("e2e/fixtures/pump.pdf")
    const uploadResponse = await uploadResponsePromise
    expect(uploadResponse.ok()).toBeTruthy()
    const uploadedDocument = await uploadResponse.json() as { id: string; folder: string; source_id: string }
    expect(uploadedDocument.folder).toBe(uploadFolder)
    expect(uploadedDocument.source_id).toBe(source.id)

    const documentRow = page.getByRole("row").filter({ hasText: "pump.pdf" })
    await expect(documentRow).toBeVisible()

    const sourceFilter = page.getByLabel(/filter by source|按来源筛选/i)
    const emptyFilterResponsePromise = page.waitForResponse((response) => {
      const url = new URL(response.url())
      return url.pathname === `/api/knowledge/${ksId}/documents/page`
        && response.request().method() === "GET"
        && url.searchParams.get("source_id") === emptySource.id
    })
    await sourceFilter.click()
    await page.getByRole("option", { name: emptySourceName, exact: true }).click()
    const emptyFilterResponse = await emptyFilterResponsePromise
    expect(emptyFilterResponse.ok()).toBeTruthy()
    await expect(documentRow).toHaveCount(0)
    await expect(page.getByText(/this folder is empty|此文件夹为空/i)).toBeVisible()

    const allSourcesResponsePromise = page.waitForResponse((response) => {
      const url = new URL(response.url())
      return url.pathname === `/api/knowledge/${ksId}/documents/page`
        && response.request().method() === "GET"
        && url.searchParams.get("source_id") === null
    })
    await sourceFilter.click()
    await page.getByRole("option", { name: /all sources|全部来源/i }).click()
    const allSourcesResponse = await allSourcesResponsePromise
    expect(allSourcesResponse.ok()).toBeTruthy()
    await expect(documentRow).toBeVisible()
    await expect(page.getByLabel(/upload source/i)).toHaveText(sourceName)

    await documentRow.getByRole("button", { name: /move/i }).click()
    const moveDialog = page.getByRole("dialog")
    const movedFolder = `/source-ui-moved-${Date.now()}`
    await moveDialog.getByRole("textbox").fill(movedFolder)
    const moveResponsePromise = page.waitForResponse((response) => {
      const url = new URL(response.url())
      return url.pathname === `/api/knowledge/${ksId}/documents/${uploadedDocument.id}`
        && response.request().method() === "PATCH"
    })
    await moveDialog.getByRole("button", { name: /move/i }).click()
    const moveResponse = await moveResponsePromise
    expect(moveResponse.ok()).toBeTruthy()
    const movedDocument = await moveResponse.json() as { folder: string; source_id: string }
    expect(movedDocument.folder).toBe(movedFolder)
    expect(movedDocument.source_id).toBe(source.id)
  })

  test("keeps Sources read-only for viewers and denies users without KS access", async ({ page }) => {
    test.skip(!usesIsolatedSourceTestDatabase(), "Viewer access contracts require the isolated SQLite E2E database.")

    await loginAsAdmin(page)
    const origin = new URL(page.url()).origin
    const suffix = `${Date.now()}-${Math.random().toString(16).slice(2)}`
    const viewerUsername = `viewer-e2e-${suffix}`
    const outsiderUsername = `outsider-e2e-${suffix}`
    const password = `Viewer-e2e-${suffix}-private`

    const viewerResponse = await page.request.post(`${origin}/api/auth/users`, {
      data: { username: viewerUsername, password, is_admin: false },
    })
    expect(viewerResponse.ok()).toBeTruthy()
    const outsiderResponse = await page.request.post(`${origin}/api/auth/users`, {
      data: { username: outsiderUsername, password, is_admin: false },
    })
    expect(outsiderResponse.ok()).toBeTruthy()

    const systemResponse = await page.request.post(`${origin}/api/knowledge`, {
      data: { name: `Viewer Source E2E ${suffix}`, description: "Temporary isolated Viewer contract" },
    })
    expect(systemResponse.ok()).toBeTruthy()
    const ksId = (await systemResponse.json() as { id: string }).id
    const sourceName = `Viewer folder ${suffix}`
    const sourceResponse = await page.request.post(`${origin}/api/knowledge/${ksId}/ingestion-sources`, {
      data: { kind: "folder", name: sourceName },
    })
    expect(sourceResponse.ok()).toBeTruthy()

    const memberResponse = await page.request.post(`${origin}/api/knowledge/${ksId}/members`, {
      data: { username: viewerUsername, role: "viewer" },
    })
    expect(memberResponse.ok()).toBeTruthy()

    await page.context().clearCookies()
    await loginAsAdmin(page, { username: viewerUsername, password })
    await page.goto(`/knowledge/${ksId}/documents?tab=sources`)

    await expect(page.getByRole("heading", { name: /sources|来源/i })).toBeVisible()
    await expect(page.getByText(sourceName, { exact: true })).toBeVisible()
    await expect(page.getByRole("button", { name: /add source|添加来源/i })).toHaveCount(0)
    await expect(page.getByRole("columnheader", { name: /actions|操作/i })).toHaveCount(0)
    await expect(page.getByRole("button", { name: /edit|delete|sync now|manage.*token|编辑|删除|同步|令牌/i })).toHaveCount(0)

    await page.getByRole("tab", { name: /documents|文档/i }).click()
    await expect(page.getByRole("button", { name: /upload here|上传到当前目录/i })).toHaveCount(0)
    await expect(page.getByRole("button", { name: /new folder|新建文件夹/i })).toHaveCount(0)
    await expect(page.locator("#document-upload-source")).toHaveCount(0)
    await page.evaluate(() => {
      const transfer = new DataTransfer()
      transfer.items.add(new File(["viewer upload must not start"], "viewer.txt", { type: "text/plain" }))
      window.dispatchEvent(new DragEvent("dragenter", { bubbles: true, cancelable: true, dataTransfer: transfer }))
    })
    await expect(page.getByText(/drag and drop or click to upload|拖放文件/i)).toHaveCount(0)

    await page.context().clearCookies()
    await loginAsAdmin(page, { username: outsiderUsername, password })
    const deniedResponse = await page.request.get(`${origin}/api/knowledge/${ksId}`)
    expect(deniedResponse.status()).toBe(403)
  })
})