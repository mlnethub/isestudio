import { Copy, KeyRound, Loader2, Pencil, Plus, RefreshCw, Trash2 } from "lucide-react"
import { useEffect, useRef, useState, type FormEvent } from "react"
import { toast } from "sonner"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import IngestionSourceSchedulePicker from "@/components/IngestionSourceSchedulePicker"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { api } from "@/lib/api"
import { useConfirm } from "@/lib/confirm"
import { useI18n } from "@/lib/i18n"
import { sourceConfigFieldLabel, sourceKindLabel } from "@/lib/ingestionSourceLabels"
import { waitForIngestionSourceJob } from "@/lib/ingestionSourceSync"
import { scheduleForSourceKind, scheduleToPickerState, type IngestionSourceSchedulePickerState } from "@/lib/ingestionSourceSchedule"
import type { IngestionSource, IngestionSourceDetail, IngestionSourceKind, IngestionSourceRun } from "@/lib/types"

type SourceForm = {
  id: string | null
  kind: string
  name: string
  config: Record<string, string | boolean>
  schedule: IngestionSourceSchedulePickerState
}

function formatDate(value: string, locale: string) {
  return new Date(value).toLocaleString(locale)
}

function RunHistory({ ksId, sourceId, revision }: { ksId: string; sourceId: string; revision: number }) {
  const { locale, t } = useI18n()
  const [runs, setRuns] = useState<IngestionSourceRun[] | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let active = true
    void api.listIngestionSourceRuns(ksId, sourceId)
      .then((result) => { if (active) setRuns(result.slice(0, 50)) })
      .catch(() => { if (active) setFailed(true) })
    return () => { active = false }
  }, [ksId, sourceId, revision])

  if (failed) return <p className="text-xs text-muted-foreground">{t("ingestionSources.runsLoadFailed")}</p>
  if (!runs) return <p className="text-xs text-muted-foreground">{t("common.loading")}</p>
  if (runs.length === 0) return <p className="text-xs text-muted-foreground">{t("ingestionSources.noRuns")}</p>

  return (
    <ul className="space-y-1.5">
      {runs.map((run) => (
        <li key={run.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
          <Badge variant={run.status === "failed" ? "destructive" : "secondary"}>
            {run.status === "failed"
              ? t("ingestionSources.run.failed")
              : t(`ingestionSources.status.${run.status}`)}
          </Badge>
          <time className="text-muted-foreground" dateTime={run.started_at}>
            {formatDate(run.started_at, locale)}
          </time>
          <span>{t("ingestionSources.run.added", { count: run.added_count })}</span>
          <span>{t("ingestionSources.run.updated", { count: run.updated_count })}</span>
        </li>
      ))}
    </ul>
  )
}

export default function IngestionSourcesPanel({
  ksId,
  canWrite = false,
  onChanged,
}: {
  ksId: string
  canWrite?: boolean
  onChanged?: () => void
}) {
  const { locale, t } = useI18n()
  const confirmAction = useConfirm()
  const [sources, setSources] = useState<IngestionSource[] | null>(null)
  const [kinds, setKinds] = useState<IngestionSourceKind[]>([])
  const [failed, setFailed] = useState(false)
  const [reload, setReload] = useState(0)
  const [form, setForm] = useState<SourceForm | null>(null)
  const [formError, setFormError] = useState("")
  const [saving, setSaving] = useState(false)
  const [deletingId, setDeletingId] = useState<string | null>(null)
  const [syncingId, setSyncingId] = useState<string | null>(null)
  const [tokenSource, setTokenSource] = useState<IngestionSource | null>(null)
  const [tokenValue, setTokenValue] = useState("")
  const [tokenBusy, setTokenBusy] = useState(false)
  const [tokenError, setTokenError] = useState("")
  const syncAbortRef = useRef<AbortController | null>(null)
  const tokenRequestRef = useRef(0)

  useEffect(() => {
    if (syncAbortRef.current) {
      syncAbortRef.current.abort()
      syncAbortRef.current = null
      setSyncingId(null)
    }
    tokenRequestRef.current += 1
    setTokenSource(null)
    setTokenValue("")
    setTokenBusy(false)
    setTokenError("")
    return () => {
      syncAbortRef.current?.abort()
      tokenRequestRef.current += 1
    }
  }, [ksId])

  useEffect(() => {
    let active = true
    setSources(null)
    setFailed(false)
    void Promise.all([api.listIngestionSources(ksId), api.getIngestionSourceKinds(ksId)])
      .then(([result, availableKinds]) => {
        if (!active) return
        setSources(result)
        setKinds(availableKinds)
      })
      .catch(() => { if (active) setFailed(true) })
    return () => { active = false }
  }, [ksId, reload])

  const openCreate = () => {
    const kind = kinds[0]
    if (!kind) return
    setForm({ id: null, kind: kind.kind, name: "", config: {}, schedule: scheduleToPickerState() })
    setFormError("")
  }

  const openEdit = async (source: IngestionSource) => {
    setFormError("")
    try {
      const detail = await api.getIngestionSource(ksId, source.id)
      setForm(formFromDetail(detail, kinds))
    } catch (error) {
      toast.error(t("common.failedLoad", { error: (error as Error).message }))
    }
  }

  const saveSource = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!form) return
    const kind = kinds.find((item) => item.kind === form.kind)
    if (!kind) return
    setSaving(true)
    setFormError("")
    try {
      const body = {
        kind: form.kind,
        name: form.name.trim(),
        config: configForRequest(form, kind),
        ...scheduleForSourceKind(kind.active_sync, form.schedule),
      }
      if (form.id) await api.updateIngestionSource(ksId, form.id, body)
      else await api.createIngestionSource(ksId, body)
      setForm(null)
      setReload((value) => value + 1)
      onChanged?.()
      toast.success(t(form.id ? "ingestionSources.updated" : "ingestionSources.created"))
    } catch (error) {
      setFormError((error as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const deleteSource = async (source: IngestionSource) => {
    if (!await confirmAction(t("ingestionSources.deleteConfirm", { name: source.name }), {
      destructive: true,
    })) return
    setDeletingId(source.id)
    try {
      await api.deleteIngestionSource(ksId, source.id)
      setReload((value) => value + 1)
      onChanged?.()
      toast.success(t("common.deleted"))
    } catch (error) {
      toast.error(t("common.failedDelete", { error: (error as Error).message }))
    } finally {
      setDeletingId(null)
    }
  }

  const syncSource = async (source: IngestionSource) => {
    if (syncAbortRef.current) return
    const controller = new AbortController()
    syncAbortRef.current = controller
    setSyncingId(source.id)
    try {
      const accepted = await api.syncIngestionSource(ksId, source.id)
      const job = await waitForIngestionSourceJob(
        () => api.getIngestionSourceJob(ksId, source.id, accepted.job_id),
        controller.signal,
      )
      if (controller.signal.aborted) return
      setReload((value) => value + 1)
      onChanged?.()
      toast[job.status === "ok" ? "success" : "error"](
        t(job.status === "ok" ? "ingestionSources.syncSucceeded" : "ingestionSources.syncFailed"),
      )
    } catch {
      if (!controller.signal.aborted) toast.error(t("ingestionSources.syncFailed"))
    } finally {
      if (syncAbortRef.current === controller) syncAbortRef.current = null
      if (!controller.signal.aborted) setSyncingId(null)
    }
  }

  const closeTokenDialog = () => {
    if (tokenBusy) return
    tokenRequestRef.current += 1
    setTokenSource(null)
    setTokenValue("")
    setTokenError("")
  }

  const openTokenDialog = (source: IngestionSource) => {
    tokenRequestRef.current += 1
    setTokenSource(source)
    setTokenValue("")
    setTokenError("")
  }

  const requestToken = async (operation: "reveal" | "rotate") => {
    if (!tokenSource || tokenBusy) return
    const dialogGeneration = tokenRequestRef.current
    if (operation === "rotate" && !await confirmAction(t("ingestionSources.token.rotateConfirm"), {
      destructive: true,
    })) return
    if (tokenRequestRef.current !== dialogGeneration) return

    const requestId = ++tokenRequestRef.current
    setTokenBusy(true)
    setTokenError("")
    setTokenValue("")
    try {
      const result = operation === "reveal"
        ? await api.revealIngestionSourceToken(ksId, tokenSource.id)
        : await api.rotateIngestionSourceToken(ksId, tokenSource.id)
      if (tokenRequestRef.current === requestId) setTokenValue(result.token)
    } catch {
      if (tokenRequestRef.current === requestId) setTokenError(t("ingestionSources.token.operationFailed"))
    } finally {
      if (tokenRequestRef.current === requestId) setTokenBusy(false)
    }
  }

  const copyToken = async () => {
    if (!tokenValue) return
    try {
      await navigator.clipboard.writeText(tokenValue)
      toast.success(t("ingestionSources.token.copied"))
    } catch {
      toast.error(t("ingestionSources.token.copyFailed"))
    }
  }

  return (
    <section className="min-w-0 space-y-3" aria-labelledby="ingestion-sources-title">
      <div className="flex items-center justify-between gap-3">
        <h2 id="ingestion-sources-title" className="text-sm font-semibold">
          {t("ingestionSources.title")}
        </h2>
        <div className="flex items-center gap-1">
          {canWrite && kinds.length > 0 && (
            <Button size="sm" variant="outline" onClick={openCreate}>
              <Plus className="h-4 w-4" />{t("ingestionSources.add")}
            </Button>
          )}
          <Button
            size="icon-sm"
            variant="ghost"
            title={t("common.refresh")}
            aria-label={t("common.refresh")}
            onClick={() => setReload((value) => value + 1)}
          >
            <RefreshCw className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {failed ? (
        <div role="alert" className="border-y py-6 text-center text-sm text-destructive">
          {t("ingestionSources.loadFailed")}
        </div>
      ) : sources === null ? (
        <div role="status" className="border-y py-6 text-center text-sm text-muted-foreground">
          {t("common.loading")}
        </div>
      ) : sources.length === 0 ? (
        <div className="border-y py-6 text-center text-sm text-muted-foreground">
          {t("ingestionSources.empty")}
        </div>
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("common.name")}</TableHead>
              <TableHead>{t("ingestionSources.kind")}</TableHead>
              <TableHead>{t("ingestionSources.status")}</TableHead>
              <TableHead>{t("ingestionSources.lastSynced")}</TableHead>
              <TableHead>{t("ingestionSources.documents")}</TableHead>
              {canWrite && <TableHead>{t("common.actions")}</TableHead>}
            </TableRow>
          </TableHeader>
          <TableBody>
            {sources.map((source) => (
              <SourceRows
                key={source.id}
                source={source}
                ksId={ksId}
                locale={locale}
                t={t}
                canWrite={canWrite}
                syncable={kinds.some((kind) => kind.kind === source.kind && kind.active_sync)}
                syncing={syncingId === source.id}
                syncDisabled={syncingId !== null}
                onSync={() => { void syncSource(source) }}
                tokenSupported={source.supports_push_token}
                onManageToken={() => openTokenDialog(source)}
                runsRevision={reload}
                deleting={deletingId === source.id}
                onEdit={() => { void openEdit(source) }}
                onDelete={() => { void deleteSource(source) }}
              />
            ))}
          </TableBody>
        </Table>
      )}

      <Dialog open={form !== null} onOpenChange={(open) => { if (!open && !saving) setForm(null) }}>
        <DialogContent className="sm:max-w-lg">
          <DialogHeader>
            <DialogTitle>{t(form?.id ? "ingestionSources.edit" : "ingestionSources.add")}</DialogTitle>
          </DialogHeader>
          {form && (
            <form id="ingestion-source-form" className="space-y-4" onSubmit={saveSource}>
              <div className="space-y-2">
                <Label>{t("ingestionSources.kind")}</Label>
                <Select
                  value={form.kind}
                  disabled={form.id !== null}
                  onValueChange={(kind) => setForm({
                    ...form,
                    kind,
                    config: {},
                    schedule: scheduleToPickerState(),
                  })}
                >
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {kinds.map((kind) => (
                      <SelectItem key={kind.kind} value={kind.kind}>{sourceKindLabel(kind.kind, t)}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <div className="space-y-2">
                <Label htmlFor="ingestion-source-name">{t("common.name")}</Label>
                <Input
                  id="ingestion-source-name"
                  autoFocus
                  maxLength={255}
                  required
                  value={form.name}
                  onChange={(event) => setForm({ ...form, name: event.target.value })}
                />
              </div>
              {kinds.find((kind) => kind.kind === form.kind)?.config_fields.map((field) => (
                <div key={field.name} className="space-y-2">
                  <Label htmlFor={`source-config-${field.name}`}>
                    {sourceConfigFieldLabel(field.name, t)}{field.required ? " *" : ""}
                  </Label>
                  <Input
                    id={`source-config-${field.name}`}
                    type={field.secret ? "password" : field.type === "integer" || field.type === "number" ? "number" : "text"}
                    autoComplete={field.secret ? "new-password" : undefined}
                    required={field.required && (!field.secret || form.id === null)}
                    value={String(form.config[field.name] ?? "")}
                    onChange={(event) => setForm({
                      ...form,
                      config: { ...form.config, [field.name]: event.target.value },
                    })}
                  />
                </div>
              ))}
              {kinds.find((kind) => kind.kind === form.kind)?.active_sync && (
                <IngestionSourceSchedulePicker
                  value={form.schedule}
                  onChange={(schedule) => setForm({ ...form, schedule })}
                />
              )}
              {formError && <p role="alert" className="text-sm text-destructive">{formError}</p>}
            </form>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setForm(null)} disabled={saving}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" form="ingestion-source-form" disabled={saving || !form?.name.trim()}>
              {saving ? t("common.loading") : t("common.save")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={tokenSource !== null} onOpenChange={(open) => { if (!open) closeTokenDialog() }}>
        <DialogContent className="sm:max-w-lg">
          <DialogHeader>
            <DialogTitle>{t("ingestionSources.token.title", { name: tokenSource?.name ?? "" })}</DialogTitle>
          </DialogHeader>
          <div className="space-y-4">
            <p className="text-sm text-muted-foreground">{t("ingestionSources.token.help")}</p>
            {tokenValue && (
              <div className="flex gap-2">
                <Input aria-label={t("ingestionSources.token.value")} readOnly value={tokenValue} />
                <Button type="button" variant="outline" onClick={() => { void copyToken() }}>
                  <Copy className="h-4 w-4" />{t("ingestionSources.token.copy")}
                </Button>
              </div>
            )}
            {tokenError && <p role="alert" className="text-sm text-destructive">{tokenError}</p>}
          </div>
          <DialogFooter className="flex-col-reverse gap-2 sm:flex-row">
            <Button type="button" variant="outline" onClick={closeTokenDialog} disabled={tokenBusy}>
              {t("common.close")}
            </Button>
            <Button type="button" variant="outline" onClick={() => { void requestToken("reveal") }} disabled={tokenBusy}>
              {tokenBusy ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              {t("ingestionSources.token.reveal")}
            </Button>
            <Button type="button" variant="destructive" onClick={() => { void requestToken("rotate") }} disabled={tokenBusy}>
              {t("ingestionSources.token.rotate")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </section>
  )
}

function SourceRows({
  source,
  ksId,
  locale,
  t,
  canWrite,
  syncable,
  syncing,
  syncDisabled,
  onSync,
  tokenSupported,
  onManageToken,
  runsRevision,
  deleting,
  onEdit,
  onDelete,
}: {
  source: IngestionSource
  ksId: string
  locale: string
  t: ReturnType<typeof useI18n>["t"]
  canWrite: boolean
  syncable: boolean
  syncing: boolean
  syncDisabled: boolean
  onSync: () => void
  tokenSupported: boolean
  onManageToken: () => void
  runsRevision: number
  deleting: boolean
  onEdit: () => void
  onDelete: () => void
}) {
  const colSpan = canWrite ? 6 : 5
  return (
    <>
      <TableRow>
        <TableCell className="font-medium">{source.name}</TableCell>
        <TableCell>{sourceKindLabel(source.kind, t)}</TableCell>
        <TableCell>
          <Badge variant={source.last_sync_status === "failed" ? "destructive" : "secondary"}>
            {t(`ingestionSources.status.${source.last_sync_status}`)}
          </Badge>
        </TableCell>
        <TableCell>
          {source.last_synced_at
            ? <time dateTime={source.last_synced_at}>{formatDate(source.last_synced_at, locale)}</time>
            : <span className="text-muted-foreground">{t("ingestionSources.neverSynced")}</span>}
        </TableCell>
        <TableCell>
          <span className="tabular-nums">{source.document_count}</span>
          <span className="text-muted-foreground">
            {" · "}{source.missing_document_count} {t("ingestionSources.missing")}
          </span>
        </TableCell>
        {canWrite && (
          <TableCell>
            <div className="flex items-center gap-1">
              {syncable && (
                <Button
                  size="icon-sm"
                  variant="ghost"
                  title={t(syncing ? "ingestionSources.syncing" : "ingestionSources.sync")}
                  aria-label={t(syncing ? "ingestionSources.syncing" : "ingestionSources.sync")}
                  disabled={syncDisabled}
                  onClick={onSync}
                >
                  {syncing ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                </Button>
              )}
              {canWrite && tokenSupported && (
                <Button
                  size="icon-sm"
                  variant="ghost"
                  title={t("ingestionSources.token.manage")}
                  aria-label={t("ingestionSources.token.manage")}
                  onClick={onManageToken}
                >
                  <KeyRound className="h-4 w-4" />
                </Button>
              )}
              <Button size="icon-sm" variant="ghost" title={t("common.edit")} aria-label={t("common.edit")} onClick={onEdit}>
                <Pencil className="h-4 w-4" />
              </Button>
              <Button
                size="icon-sm"
                variant="ghost"
                title={t("common.delete")}
                aria-label={t("common.delete")}
                disabled={deleting}
                onClick={onDelete}
              >
                <Trash2 className="h-4 w-4" />
              </Button>
            </div>
          </TableCell>
        )}
      </TableRow>
      <TableRow>
        <TableCell colSpan={colSpan} className="bg-muted/20 py-2.5">
          <div className="space-y-2 pl-2">
            <h3 className="text-xs font-medium text-muted-foreground">
              {t("ingestionSources.runHistory")}
            </h3>
            <RunHistory ksId={ksId} sourceId={source.id} revision={runsRevision} />
          </div>
        </TableCell>
      </TableRow>
    </>
  )
}

function formFromDetail(detail: IngestionSourceDetail, kinds: IngestionSourceKind[]): SourceForm {
  const kind = kinds.find((item) => item.kind === detail.kind)
  const fields = kind?.config_fields ?? []
  const config = Object.fromEntries(fields
    .filter((field) => !field.secret && detail.config[field.name] !== undefined)
    .map((field) => [field.name, detail.config[field.name] as string | boolean]))
  const schedule = kind?.active_sync
    ? scheduleToPickerState(detail)
    : scheduleToPickerState()
  return { id: detail.id, kind: detail.kind, name: detail.name, config, schedule }
}

function configForRequest(form: SourceForm, kind: IngestionSourceKind): Record<string, unknown> | undefined {
  const config: Record<string, unknown> = {}
  for (const field of kind.config_fields) {
    const value = form.config[field.name]
    if (value === undefined || value === "") continue
    if (field.type === "boolean") config[field.name] = value === true || value === "true"
    else if (field.type === "integer" || field.type === "number") config[field.name] = Number(value)
    else config[field.name] = value
  }
  return Object.keys(config).length ? config : undefined
}