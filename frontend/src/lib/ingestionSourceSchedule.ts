export type IngestionSourceScheduleMode = "manual" | "interval" | "daily" | "weekly" | "advanced"

export interface IngestionSourceSchedule {
  sync_interval_minutes: number | null
  sync_cron: string | null
}

export interface IngestionSourceSchedulePickerState {
  mode: IngestionSourceScheduleMode
  every?: number
  unit?: "minutes" | "hours"
  time?: string
  days?: number[]
  cron?: string
}

const dayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"]

function pad(value: string): string {
  return value.padStart(2, "0")
}

function dayIndex(value: string): number | null {
  const normalized = value.toUpperCase()
  const aliases: Record<string, number> = {
    SUN: 0, SUNDAY: 0, MON: 1, MONDAY: 1, TUE: 2, TUESDAY: 2,
    WED: 3, WEDNESDAY: 3, THU: 4, THURSDAY: 4, FRI: 5, FRIDAY: 5,
    SAT: 6, SATURDAY: 6,
  }
  if (normalized in aliases) return aliases[normalized]
  if (/^[0-7]$/.test(normalized)) return Number(normalized) % 7
  return null
}

export function scheduleToPickerState(
  initial?: IngestionSourceSchedule | null,
): IngestionSourceSchedulePickerState {
  const base = {
    mode: "manual" as const,
    every: 30,
    unit: "minutes" as const,
    time: "09:00",
    days: [0],
    cron: "",
  }
  if (!initial) return base

  if (initial.sync_cron) {
    const daily = initial.sync_cron.match(/^(\d{1,2})\s+(\d{1,2})\s+\*\s+\*\s+\*$/)
    if (daily) return { ...base, mode: "daily", time: `${pad(daily[2])}:${pad(daily[1])}` }

    const weekly = initial.sync_cron.match(/^(\d{1,2})\s+(\d{1,2})\s+\*\s+\*\s+([\w,]+)$/)
    if (weekly) {
      const days = weekly[3].split(",").map(dayIndex)
      if (days.length > 0 && days.every((day): day is number => day !== null)) {
        return {
          ...base,
          mode: "weekly",
          time: `${pad(weekly[2])}:${pad(weekly[1])}`,
          days: [...new Set(days)].sort((left, right) => left - right),
        }
      }
    }
    return { ...base, mode: "advanced", cron: initial.sync_cron }
  }

  if (initial.sync_interval_minutes && initial.sync_interval_minutes > 0) {
    const minutes = initial.sync_interval_minutes
    return minutes % 60 === 0
      ? { ...base, mode: "interval", every: minutes / 60, unit: "hours" }
      : { ...base, mode: "interval", every: minutes, unit: "minutes" }
  }
  return base
}

export function scheduleFromPickerState(
  state: IngestionSourceSchedulePickerState,
): IngestionSourceSchedule {
  switch (state.mode) {
    case "interval": {
      const every = Math.max(1, Math.trunc(state.every ?? 30))
      return {
        sync_interval_minutes: state.unit === "hours" ? every * 60 : every,
        sync_cron: null,
      }
    }
    case "daily": {
      const [hour, minute] = (state.time ?? "09:00").split(":").map(Number)
      return { sync_interval_minutes: null, sync_cron: `${minute} ${hour} * * *` }
    }
    case "weekly": {
      const days = [...new Set(state.days ?? [])].sort((left, right) => left - right)
      const [hour, minute] = (state.time ?? "09:00").split(":").map(Number)
      return {
        sync_interval_minutes: null,
        sync_cron: days.length ? `${minute} ${hour} * * ${days.map((day) => dayNames[day]).join(",")}` : null,
      }
    }
    case "advanced":
      return { sync_interval_minutes: null, sync_cron: state.cron?.trim() || null }
    default:
      return { sync_interval_minutes: null, sync_cron: null }
  }
}

export function scheduleForSourceKind(
  activeSync: boolean,
  state: IngestionSourceSchedulePickerState,
): IngestionSourceSchedule {
  return activeSync
    ? scheduleFromPickerState(state)
    : { sync_interval_minutes: null, sync_cron: null }
}