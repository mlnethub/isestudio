import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { useI18n } from "@/lib/i18n"
import type { IngestionSourceSchedulePickerState, IngestionSourceScheduleMode } from "@/lib/ingestionSourceSchedule"

const modes: IngestionSourceScheduleMode[] = ["manual", "interval", "daily", "weekly", "advanced"]
const weekdays = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"] as const

export default function IngestionSourceSchedulePicker({
  value,
  onChange,
}: {
  value: IngestionSourceSchedulePickerState
  onChange: (value: IngestionSourceSchedulePickerState) => void
}) {
  const { t } = useI18n()

  const setMode = (mode: IngestionSourceScheduleMode) => onChange({ ...value, mode })

  return (
    <fieldset className="space-y-2">
      <legend className="text-sm font-medium">{t("ingestionSources.schedule.title")}</legend>
      <div role="group" aria-label={t("ingestionSources.schedule.title")} className="flex flex-wrap gap-1">
        {modes.map((mode) => (
          <Button
            key={mode}
            type="button"
            size="sm"
            variant={value.mode === mode ? "default" : "outline"}
            aria-pressed={value.mode === mode}
            onClick={() => setMode(mode)}
          >
            {t(`ingestionSources.schedule.${mode}`)}
          </Button>
        ))}
      </div>

      {value.mode === "interval" && (
        <div className="flex flex-wrap items-center gap-2">
          <Label htmlFor="ingestion-source-schedule-every">{t("ingestionSources.schedule.every")}</Label>
          <Input
            id="ingestion-source-schedule-every"
            className="w-20"
            type="number"
            min={1}
            required
            value={value.every ?? 30}
            onChange={(event) => onChange({ ...value, every: Math.max(1, event.currentTarget.valueAsNumber || 1) })}
          />
          <Select
            value={value.unit ?? "minutes"}
            onValueChange={(unit: "minutes" | "hours") => onChange({ ...value, unit })}
          >
            <SelectTrigger className="w-28" aria-label={t("ingestionSources.schedule.unit")}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="minutes">{t("ingestionSources.schedule.minutes")}</SelectItem>
              <SelectItem value="hours">{t("ingestionSources.schedule.hours")}</SelectItem>
            </SelectContent>
          </Select>
        </div>
      )}

      {(value.mode === "daily" || value.mode === "weekly") && (
        <div className="space-y-2">
          {value.mode === "weekly" && (
            <div role="group" aria-label={t("ingestionSources.schedule.weekdays")} className="grid grid-cols-7 gap-1">
              {weekdays.map((day, index) => {
                const selectedDays = value.days ?? [0]
                const selected = selectedDays.includes(index)
                return (
                  <Button
                    key={day}
                    type="button"
                    size="sm"
                    variant={selected ? "default" : "outline"}
                    aria-label={t(`ingestionSources.schedule.days.${day}`)}
                    aria-pressed={selected}
                    disabled={selected && selectedDays.length === 1}
                    onClick={() => onChange({
                      ...value,
                      days: selected
                        ? selectedDays.filter((item) => item !== index)
                        : [...selectedDays, index].sort((left, right) => left - right),
                    })}
                  >
                    {t(`ingestionSources.schedule.daysShort.${day}`)}
                  </Button>
                )
              })}
            </div>
          )}
          <div className="flex items-center gap-2">
            <Label htmlFor="ingestion-source-schedule-time">{t("ingestionSources.schedule.at")}</Label>
            <Input
              id="ingestion-source-schedule-time"
              className="w-28 tabular-nums"
              type="text"
              inputMode="numeric"
              pattern="([01]?\\d|2[0-3]):[0-5]\\d"
              placeholder="09:00"
              required
              value={value.time ?? "09:00"}
              onChange={(event) => onChange({ ...value, time: event.currentTarget.value })}
            />
          </div>
        </div>
      )}

      {value.mode === "advanced" && (
        <div className="space-y-2">
          <Label htmlFor="ingestion-source-schedule-cron">{t("ingestionSources.schedule.cron")}</Label>
          <Input
            id="ingestion-source-schedule-cron"
            className="font-mono"
            placeholder={t("ingestionSources.schedule.cronPlaceholder")}
            value={value.cron ?? ""}
            onChange={(event) => onChange({ ...value, cron: event.currentTarget.value })}
          />
        </div>
      )}
    </fieldset>
  )
}