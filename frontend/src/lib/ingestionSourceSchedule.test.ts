import { describe, expect, it } from "vitest"

import { scheduleForSourceKind, scheduleFromPickerState, scheduleToPickerState } from "./ingestionSourceSchedule"

describe("ingestion source schedules", () => {
  it("defaults to manual sync", () => {
    const state = scheduleToPickerState()
    expect(state.mode).toBe("manual")
    expect(scheduleFromPickerState(state)).toEqual({
      sync_interval_minutes: null,
      sync_cron: null,
    })
  })

  it("restores interval schedules using hours when evenly divisible", () => {
    const state = scheduleToPickerState({ sync_interval_minutes: 120, sync_cron: null })

    expect(state).toMatchObject({ mode: "interval", every: 2, unit: "hours" })
    expect(scheduleFromPickerState(state)).toEqual({ sync_interval_minutes: 120, sync_cron: null })
  })

  it("restores named weekly cron days and serializes them in standard cron form", () => {
    const state = scheduleToPickerState({ sync_interval_minutes: null, sync_cron: "15 8 * * MON,WED" })

    expect(state).toMatchObject({ mode: "weekly", time: "08:15", days: [1, 3] })
    expect(scheduleFromPickerState(state)).toEqual({
      sync_interval_minutes: null,
      sync_cron: "15 8 * * MON,WED",
    })
  })

  it("keeps unrecognized cron expressions in advanced mode", () => {
    const state = scheduleToPickerState({ sync_interval_minutes: null, sync_cron: "*/15 * * * *" })

    expect(state).toMatchObject({ mode: "advanced", cron: "*/15 * * * *" })
    expect(scheduleFromPickerState(state)).toEqual({ sync_interval_minutes: null, sync_cron: "*/15 * * * *" })
  })

  it("serializes daily and manual modes with mutually exclusive fields", () => {
    expect(scheduleFromPickerState({ mode: "daily", time: "06:45" })).toEqual({
      sync_interval_minutes: null,
      sync_cron: "45 6 * * *",
    })
    expect(scheduleFromPickerState({ mode: "manual" })).toEqual({
      sync_interval_minutes: null,
      sync_cron: null,
    })
  })

  it("clears schedules for source kinds that do not support scheduled sync", () => {
    expect(scheduleForSourceKind(false, { mode: "interval", every: 15, unit: "minutes" })).toEqual({
      sync_interval_minutes: null,
      sync_cron: null,
    })
  })
})