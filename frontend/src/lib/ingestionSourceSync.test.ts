import { afterEach, describe, expect, it, vi } from "vitest"

import type { IngestionSourceJob, IngestionSourceJobStatus } from "./types"
import { waitForIngestionSourceJob } from "./ingestionSourceSync"

function makeJob(status: IngestionSourceJobStatus): IngestionSourceJob {
  return {
    id: "job-1",
    status,
    active_run_id: null,
    lease_until: null,
    created_at: "2026-09-29T00:00:00Z",
    started_at: null,
    finished_at: null,
    error: null,
  }
}

afterEach(() => {
  vi.useRealTimers()
})

describe("waitForIngestionSourceJob", () => {
  it("polls queued and running jobs until a terminal result", async () => {
    vi.useFakeTimers()
    const responses = [makeJob("queued"), makeJob("running"), makeJob("ok")]
    const getJob = vi.fn(async () => responses.shift()!)
    const controller = new AbortController()

    const result = waitForIngestionSourceJob(getJob, controller.signal, 1000)
    await vi.runAllTimersAsync()

    await expect(result).resolves.toEqual(makeJob("ok"))
    expect(getJob).toHaveBeenCalledTimes(3)
  })

  it("returns a failed job as a terminal result without polling again", async () => {
    const failedJob = makeJob("failed")
    const getJob = vi.fn(async () => failedJob)

    await expect(waitForIngestionSourceJob(getJob, new AbortController().signal)).resolves.toEqual(failedJob)
    expect(getJob).toHaveBeenCalledTimes(1)
  })

  it("rejects on abort and clears the pending poll timer", async () => {
    vi.useFakeTimers()
    const getJob = vi.fn(async () => makeJob("queued"))
    const controller = new AbortController()
    const result = waitForIngestionSourceJob(getJob, controller.signal, 1000)

    await Promise.resolve()
    await Promise.resolve()
    expect(vi.getTimerCount()).toBe(1)

    controller.abort()

    await expect(result).rejects.toMatchObject({ name: "AbortError" })
    expect(vi.getTimerCount()).toBe(0)
  })
})