import type { ReactNode } from "react"
import { NavLink, Navigate, Route, Routes, useLocation } from "react-router-dom"
import { Loader2, LogOut } from "lucide-react"
import { cn } from "@/lib/utils"
import { useAuth } from "@/lib/auth"
import { useI18n } from "@/lib/i18n"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { SidebarProvider } from "@/components/ui/sidebar"
import SideNav from "@/components/SideNav"
import LoginPage from "@/pages/LoginPage"
import KnowledgePage from "@/pages/KnowledgePage"
import OntologyPage from "@/pages/OntologyPage"
import DocumentDetailPage from "@/pages/DocumentDetailPage"
import DocumentationPage from "@/pages/ApiDocsPage"
import SettingsLayout from "@/pages/settings/SettingsLayout"

function TopLink({ to, active, children }: { to: string; active: boolean; children: ReactNode }) {
  return (
    <NavLink
      to={to}
      className={cn(
        "whitespace-nowrap rounded-md px-2 py-1.5 text-sm font-medium transition-colors sm:px-3",
        active ? "text-foreground" : "text-muted-foreground hover:text-foreground",
      )}
    >
      {children}
    </NavLink>
  )
}

export default function App() {
  const { user, loading, logout } = useAuth()
  const { t } = useI18n()
  const seg = useLocation().pathname.split("/").filter(Boolean)

  if (loading) {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
      </div>
    )
  }

  if (!user) return <LoginPage />

  const onKnowledge = seg.length === 0 || seg[0] === "knowledge"
  const onDocs = seg[0] === "docs" || seg[0] === "api-doc"
  const onSettings = seg[0] === "settings"

  return (
    <div className="flex min-h-svh flex-col bg-background text-foreground">
      {/* Fixed global nav — same across the whole app. */}
      <header className="sticky top-0 z-30 flex h-14 shrink-0 items-center gap-2 border-b bg-background/90 px-3 backdrop-blur sm:gap-4 md:px-6">
        <NavLink to="/" className="flex items-center">
          <span className="text-sm font-medium tracking-tight">OntoPilot</span>
        </NavLink>
        <nav className="flex min-w-0 flex-1 items-center gap-0 overflow-x-auto sm:gap-1">
          <TopLink to="/" active={onKnowledge}>{t("nav.knowledgeSystems")}</TopLink>
          <TopLink to="/docs" active={onDocs}>{t("nav.apiDocs")}</TopLink>
          <TopLink to="/settings" active={onSettings}>{t("nav.settings")}</TopLink>
        </nav>
        <div className="ml-auto flex shrink-0 items-center gap-2">
          <span className="hidden items-center gap-1.5 text-sm text-muted-foreground lg:flex">
            {user.display_name || user.username}
            {user.is_admin && <Badge variant="secondary" className="text-[10px]">{t("common.admin")}</Badge>}
          </span>
          <Button variant="ghost" size="sm" onClick={() => logout()} title={t("nav.logout")}>
            <LogOut className="h-4 w-4" /> <span className="hidden sm:inline">{t("nav.logout")}</span>
          </Button>
        </div>
      </header>

      {/* Body: flush left sidebar (inside a KS) + content. SidebarProvider supplies the
          context the shadcn Sidebar primitives in <SideNav /> require. */}
      <SidebarProvider className="min-h-0 flex-1 items-start">
        <SideNav />
        <main className="min-w-0 flex-1 p-4 md:p-6">
          <Routes>
            <Route path="/" element={<KnowledgePage />} />
            <Route path="/docs" element={<DocumentationPage />} />
            <Route path="/docs/:docId" element={<DocumentationPage />} />
            <Route path="/api-doc" element={<Navigate to="/docs" replace />} />
            <Route path="/knowledge/:id" element={<OntologyPage />} />
            <Route path="/knowledge/:id/documents/:documentId" element={<DocumentDetailPage />} />
            <Route path="/knowledge/:id/:section" element={<OntologyPage />} />
            <Route path="/knowledge/:id/:section/:sub" element={<OntologyPage />} />
            {/* Users + model config now live under Settings; keep the old /users path working. */}
            <Route path="/users" element={<Navigate to="/settings/users" replace />} />
            <Route path="/settings" element={<Navigate to="/settings/profile" replace />} />
            <Route path="/settings/:section" element={<SettingsLayout />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </main>
      </SidebarProvider>
    </div>
  )
}
