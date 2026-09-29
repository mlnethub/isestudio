import { SlidersHorizontal } from "lucide-react"
import { useState } from "react"
import { Link, useSearchParams } from "react-router-dom"

import { useI18n } from "@/lib/i18n"
import { Button } from "@/components/ui/button"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import ExtractionQueuePanel from "@/components/ExtractionQueuePanel"
import IngestionSourcesPanel from "@/components/IngestionSourcesPanel"
import KsDocuments from "@/components/KsDocuments"

export default function DocumentsExtractionPanel({
  ksId,
  canWrite,
  onChanged,
}: {
  ksId: string
  canWrite: boolean
  onChanged?: () => void
}) {
  const { t } = useI18n()
  const [searchParams, setSearchParams] = useSearchParams()
  const [sourcesRevision, setSourcesRevision] = useState(0)
  const requestedTab = searchParams.get("tab")
  const tab = requestedTab === "queue" || requestedTab === "sources" ? requestedTab : "documents"

  const changeTab = (value: string) => {
    const next = new URLSearchParams(searchParams)
    if (value === "documents") next.delete("tab")
    else next.set("tab", value)
    setSearchParams(next, { replace: true })
  }

  const sourcesChanged = () => {
    setSourcesRevision((revision) => revision + 1)
    onChanged?.()
  }

  return (
    <Tabs value={tab} onValueChange={changeTab} className="min-w-0 gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <TabsList>
          <TabsTrigger value="documents">{t("documentsWorkspace.files")}</TabsTrigger>
          <TabsTrigger value="queue">{t("extractionQueue.title")}</TabsTrigger>
          <TabsTrigger value="sources">{t("documentsWorkspace.sources")}</TabsTrigger>
        </TabsList>
        <Button asChild size="sm" variant="outline">
          <Link to={`/knowledge/${ksId}/prompts`}>
            <SlidersHorizontal className="h-4 w-4" />
            {t("documentsWorkspace.promptSettings")}
          </Link>
        </Button>
      </div>

      <TabsContent value="documents" className="min-w-0">
        <KsDocuments
          ksId={ksId}
          canWrite={canWrite}
          onChanged={onChanged}
          sourcesRevision={sourcesRevision}
        />
      </TabsContent>
      <TabsContent value="queue" className="min-w-0">
        <ExtractionQueuePanel ksId={ksId} showTitle={false} />
      </TabsContent>
      <TabsContent value="sources" className="min-w-0">
        <IngestionSourcesPanel ksId={ksId} canWrite={canWrite} onChanged={sourcesChanged} />
      </TabsContent>
    </Tabs>
  )
}
