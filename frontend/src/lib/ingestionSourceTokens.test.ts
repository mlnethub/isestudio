import { afterEach, describe, expect, it, vi } from "vitest"

vi.mock("@/lib/sso/authModel", () => ({ ssoEnabled: () => false }))
vi.mock("@/lib/sso/auth", () => ({ getAccessToken: async () => null }))

import { api } from "./api"

afterEach(() => {
  vi.unstubAllGlobals()
})

describe("ingestion source token API", () => {
  it("reveals a token through the no-store POST endpoint", async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ token: "test-token-value" }), {
        status: 200,
        headers: { "Content-Type": "application/json", "Cache-Control": "no-store" },
      }),
    )
    vi.stubGlobal("fetch", fetchMock)

    const result = await api.revealIngestionSourceToken("ks-1", "source-1")

    expect(result.token).toBe("test-token-value")
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/knowledge/ks-1/ingestion-sources/source-1/token/reveal",
      expect.objectContaining({ method: "POST", credentials: "include" }),
    )
  })

  it("rotates a token through the no-store POST endpoint", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ token: "rotated-test-token" }), {
      status: 200,
      headers: { "Content-Type": "application/json", "Cache-Control": "no-store" },
    }))
    vi.stubGlobal("fetch", fetchMock)

    await api.rotateIngestionSourceToken("ks-1", "source-2")

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/knowledge/ks-1/ingestion-sources/source-2/token/rotate",
      expect.objectContaining({ method: "POST", credentials: "include" }),
    )
  })
})