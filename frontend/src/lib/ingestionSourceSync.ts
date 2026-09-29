import type { IngestionSourceJob } from "./types"

function createAbortError(): DOMException {
  return new DOMException("The operation was aborted.", "AbortError")
}

function waitForNextPoll(signal: AbortSignal, intervalMs: number): Promise<void> {
  return new Promise((resolve, reject) => {
    const finish = () => {
      clearTimeout(timer)
      signal.removeEventListener("abort", abort)
      if (signal.aborted) reject(createAbortError())
      else resolve()
    }
    const abort = () => finish()
    const timer = setTimeout(finish, intervalMs)
    signal.addEventListener("abort", abort, { once: true })
    if (signal.aborted) abort()
  })
}

export async function waitForIngestionSourceJob(
  getJob: () => Promise<IngestionSourceJob>,
  signal: AbortSignal,
  intervalMs = 1000,
): Promise<IngestionSourceJob> {
  while (true) {
    if (signal.aborted) throw createAbortError()
    const job = await getJob()
    if (signal.aborted) throw createAbortError()
    if (job.status === "ok" || job.status === "failed") return job
    await waitForNextPoll(signal, intervalMs)
  }
}