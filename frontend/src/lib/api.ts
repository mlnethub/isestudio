// Thin typed API client. All calls go to /api which Vite proxies to the FastAPI backend.
import type {
  AboxClassList,
  ApiToken,
  ApiTokenCreated,
  ApiTokenRevealed,
  ApiTokenScope,
  AssertionInput,
  Chunk,
  Conflict,
  ConflictContext,
  HistoryResponse,
  DocumentContribution,
  DocumentImpact,
  DocumentListResponse,
  DocumentMeta,
  EditOp,
  EditResult,
  ExtractionJob,
  ExportJob,
  ExportList,
  Individual,
  IndividualList,
  KnowledgePrompt,
  KnowledgePromptList,
  KnowledgeSystem,
  GrantableUser,
  Member,
  MemberDetail,
  ModelCatalog,
  OntologyView,
  OntologyRelease,
  ParseResponse,
  ParseBatchResponse,
  ReconciliationList,
  ResolutionDecisions,
  ResolutionQueue,
  ReviewCounts,
  Provider,
  ReleaseDiff,
  ReleaseLayer,
  ReleaseList,
  RdfImportFormat,
  RdfImportResult,
  RdfImportStrategy,
  RdfImportTarget,
  ResolveResult,
  Role,
  SourceDoc,
  SystemSettings,
  TestResult,
  User,
  ValidationDecisionList,
  ValidationResult,
  VocabularyConcept,
  VocabularyConceptList,
  VocabularyConceptInput,
  VocabularyScheme,
  VocabularySchemeList,
  VocabularyView,
  TermProposal,
  TermProposalList,
} from "./types"

// The AuthProvider registers a handler here so a 401 from any call (e.g. an expired
// session) drops the app back to the login screen instead of surfacing a raw error.
let onUnauthorized: (() => void) | null = null
export function setUnauthorizedHandler(fn: (() => void) | null) {
  onUnauthorized = fn
}

function errorMessage(detail: unknown) {
  if (typeof detail === "string") return detail
  if (detail && typeof detail === "object" && "message" in detail) {
    const message = (detail as { message?: unknown }).message
    if (typeof message === "string") return message
  }
  return JSON.stringify(detail)
}

export class ApiError extends Error {
  readonly status: number
  readonly detail: unknown

  constructor(status: number, detail: unknown) {
    super(`${status}: ${errorMessage(detail)}`)
    this.name = "ApiError"
    this.status = status
    this.detail = detail
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, { credentials: "include", ...init })
  if (!res.ok) {
    if (res.status === 401 && onUnauthorized) onUnauthorized()
    let detail: unknown = res.statusText
    try {
      const body = await res.json()
      detail = body.detail ?? body
    } catch {
      /* ignore */
    }
    throw new ApiError(res.status, detail)
  }
  // Some endpoints (logout) return trivial JSON; a 204 would have no body.
  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

const json = (body: unknown): RequestInit => ({
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body),
})

const patch = (body: unknown): RequestInit => ({
  method: "PATCH",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body),
})

const put = (body: unknown): RequestInit => ({
  method: "PUT",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body),
})

export const api = {
  health: () => request<{ status: string; extract_model: string; has_llm_key: boolean }>("/api/health"),

  // Immutable ontology releases + asynchronous uncompressed N-Quads exports
  listReleases: (ksId: string) => request<ReleaseList>(`/api/knowledge/${ksId}/releases`),
  createRelease: (ksId: string, body: { version?: string; title?: string; notes?: string; shard_size?: number } = {}) =>
    request<OntologyRelease>(`/api/knowledge/${ksId}/releases`, json(body)),
  reviewRelease: (ksId: string, releaseId: string, note = "") =>
    request<OntologyRelease>(`/api/knowledge/${ksId}/releases/${releaseId}/review`, json({ note })),
  publishRelease: (ksId: string, releaseId: string, note = "") =>
    request<OntologyRelease>(`/api/knowledge/${ksId}/releases/${releaseId}/publish`, json({ note })),
  deployRelease: (ksId: string, releaseId: string) =>
    request<OntologyRelease>(`/api/knowledge/${ksId}/releases/${releaseId}/deployment`, json({})),
  stopReleaseService: (ksId: string, releaseId: string) =>
    request<OntologyRelease>(`/api/knowledge/${ksId}/releases/${releaseId}/deployment`, { method: "DELETE" }),
  deleteRelease: (ksId: string, releaseId: string) =>
    request<OntologyRelease>(`/api/knowledge/${ksId}/releases/${releaseId}`, { method: "DELETE" }),
  rollbackRelease: (ksId: string, releaseId: string) =>
    request<{ restored: number; version: string }>(`/api/knowledge/${ksId}/releases/${releaseId}/rollback`, json({})),
  diffReleases: (ksId: string, fromId: string, toId: string) =>
    request<ReleaseDiff>(`/api/knowledge/${ksId}/releases/diff?from_id=${fromId}&to_id=${toId}`),
  listExports: (ksId: string) => request<ExportList>(`/api/knowledge/${ksId}/exports`),
  createExport: (ksId: string, layer: ReleaseLayer, releaseId?: string, shardSize = 100_000) =>
    request<ExportJob>(`/api/knowledge/${ksId}/exports`, json({ layer, release_id: releaseId, shard_size: shardSize })),
  getExport: (ksId: string, jobId: string) => request<ExportJob>(`/api/knowledge/${ksId}/exports/${jobId}`),
  exportFileUrl: (ksId: string, jobId: string, filename: string) =>
    `/api/knowledge/${ksId}/exports/${jobId}/files/${encodeURIComponent(filename)}`,

  // System settings + model catalog
  getModels: () => request<ModelCatalog>("/api/models"),
  getSettings: () => request<SystemSettings>("/api/settings"),
  updateSettings: (body: {
    llm_provider_id?: string
    embedding_provider_id?: string
  }) =>
    request<SystemSettings>("/api/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),

  // Model entries (a flat list of endpoint + key + model + kind)
  listProviders: () => request<Provider[]>("/api/providers"),
  createProvider: (body: { name: string; kind: "llm" | "embedding"; base_url: string; api_key: string; model: string; concurrency_limit: number }) =>
    request<Provider>("/api/providers", json(body)),
  updateProvider: (id: string, body: { name?: string; kind?: "llm" | "embedding"; base_url?: string; api_key?: string; model?: string; concurrency_limit?: number }) =>
    request<Provider>(`/api/providers/${id}`, patch(body)),
  deleteProvider: (id: string) =>
    request<{ deleted: number }>(`/api/providers/${id}`, { method: "DELETE" }),
  testProvider: (body: {
    provider_id?: string
    base_url?: string
    api_key?: string
    model?: string
    kind?: "llm" | "embedding"
  }) => request<TestResult>("/api/providers/test", json(body)),

  // Auth
  login: (username: string, password: string) =>
    request<User>("/api/auth/login", json({ username, password })),
  logout: () => request<{ ok: boolean }>("/api/auth/logout", { method: "POST" }),
  me: () => request<User>("/api/auth/me"),
  // Self-service profile: set/clear nickname, or change password (needs current password).
  updateMe: (body: { display_name?: string; current_password?: string; new_password?: string }) =>
    request<User>("/api/auth/me", patch(body)),

  // User management (admin)
  listUsers: () => request<User[]>("/api/auth/users"),
  createUser: (username: string, password: string, is_admin = false) =>
    request<User>("/api/auth/users", json({ username, password, is_admin })),
  updateUser: (uid: string, patch: { password?: string; is_admin?: boolean; active?: boolean }) =>
    request<User>(`/api/auth/users/${uid}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(patch),
    }),
  deleteUser: (uid: string) =>
    request<{ deleted: number }>(`/api/auth/users/${uid}`, { method: "DELETE" }),

  // Documents (scoped to a knowledge system)
  listDocuments: (ksId: string) => request<DocumentMeta[]>(`/api/knowledge/${ksId}/documents`),
  getDocument: (ksId: string, id: string) =>
    request<DocumentMeta>(`/api/knowledge/${ksId}/documents/${id}`),
  listDocumentsPage: (
    ksId: string,
    params: {
      folder?: string
      q?: string
      status?: "pending" | "parsed" | "failed"
      limit?: number
      offset?: number
    } = {},
  ) => {
    const qs = new URLSearchParams()
    if (params.folder !== undefined) qs.set("folder", params.folder)
    if (params.q) qs.set("q", params.q)
    if (params.status) qs.set("status", params.status)
    qs.set("limit", String(params.limit ?? 20))
    qs.set("offset", String(params.offset ?? 0))
    return request<DocumentListResponse>(`/api/knowledge/${ksId}/documents/page?${qs.toString()}`)
  },
  uploadDocument: (ksId: string, file: File, folder = "/") => {
    const fd = new FormData()
    fd.append("file", file)
    fd.append("folder", folder)
    return request<DocumentMeta>(`/api/knowledge/${ksId}/documents/upload`, { method: "POST", body: fd })
  },
  parseDocument: (ksId: string, id: string) =>
    request<ParseResponse>(`/api/knowledge/${ksId}/documents/${id}/parse`, { method: "POST" }),
  parseDocuments: (
    ksId: string,
    body: { document_ids?: string[]; folders?: string[]; recursive?: boolean },
  ) => request<ParseBatchResponse>(
    `/api/knowledge/${ksId}/documents/parse-batch`,
    json(body),
  ),
  getChunks: (ksId: string, id: string) =>
    request<Chunk[]>(`/api/knowledge/${ksId}/documents/${id}/chunks`),
  getContribution: (ksId: string, id: string) =>
    request<DocumentContribution>(`/api/knowledge/${ksId}/documents/${id}/contribution`),
  moveDocument: (ksId: string, id: string, folder?: string, original_filename?: string) =>
    request<DocumentMeta>(`/api/knowledge/${ksId}/documents/${id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ folder, original_filename }),
    }),
  getImpact: (ksId: string, id: string) =>
    request<DocumentImpact>(`/api/knowledge/${ksId}/documents/${id}/impact`),
  deleteDocument: (ksId: string, id: string) =>
    request<{ deleted: number }>(`/api/knowledge/${ksId}/documents/${id}/delete`, { method: "POST" }),

  // Knowledge systems
  listKS: () => request<KnowledgeSystem[]>("/api/knowledge"),
  createKS: (
    name: string,
    description: string,
    opts?: { llm_provider_id?: string; embedding_provider_id?: string; llm_model?: string | null },
  ) => request<KnowledgeSystem>("/api/knowledge", json({ name, description, ...opts })),
  updateKS: (id: string, body: {
    name?: string
    description?: string
    llm_model?: string | null
    llm_provider_id?: string | null
    embedding_provider_id?: string | null
    embedding_model?: string | null
  }) => request<KnowledgeSystem>(`/api/knowledge/${id}`, patch(body)),
  getKS: (id: string) => request<KnowledgeSystem>(`/api/knowledge/${id}`),
  reviewCounts: (ksId: string) => request<ReviewCounts>(`/api/knowledge/${ksId}/review/counts`),
  deleteKS: (id: string) => request<{ deleted: number }>(`/api/knowledge/${id}`, { method: "DELETE" }),

  // Membership (owner-managed)
  listMembers: (ksId: string) => request<Member[]>(`/api/knowledge/${ksId}/members`),
  grantableUsers: (ksId: string, q?: string) =>
    request<GrantableUser[]>(`/api/knowledge/${ksId}/members/candidates${q ? `?q=${encodeURIComponent(q)}` : ""}`),
  addMember: (ksId: string, username: string, role: Role) =>
    request<Member[]>(`/api/knowledge/${ksId}/members`, json({ username, role })),
  removeMember: (ksId: string, userId: string) =>
    request<{ removed: number }>(`/api/knowledge/${ksId}/members/${userId}`, { method: "DELETE" }),
  getMemberDetail: (ksId: string, userId: string) =>
    request<MemberDetail>(`/api/knowledge/${ksId}/members/${userId}/detail`),

  // External API access (owner-managed)
  listApiTokens: (ksId: string) => request<ApiToken[]>(`/api/knowledge/${ksId}/tokens`),
  createApiToken: (
    ksId: string,
    body: { name: string; scopes: ApiTokenScope[]; expires_in_days: number | null },
  ) => request<ApiTokenCreated>(`/api/knowledge/${ksId}/tokens`, json(body)),
  revealApiToken: (ksId: string, tokenId: string) =>
    request<ApiTokenRevealed>(`/api/knowledge/${ksId}/tokens/${tokenId}/reveal`, { method: "POST" }),
  revokeApiToken: (ksId: string, tokenId: string) =>
    request<ApiToken>(`/api/knowledge/${ksId}/tokens/${tokenId}`, { method: "DELETE" }),

  // Ontology
  getOntology: (ksId: string) => request<OntologyView>(`/api/knowledge/${ksId}/ontology`),
  exportOntology: async (ksId: string, fmt: string): Promise<string> => {
    const res = await fetch(`/api/knowledge/${ksId}/ontology/export?fmt=${fmt}`, { credentials: "include" })
    if (!res.ok) {
      if (res.status === 401 && onUnauthorized) onUnauthorized()
      throw new Error(`${res.status}: ${res.statusText}`)
    }
    return res.text()
  },
  importRdf: (
    ksId: string,
    file: File,
    options: {
      target: RdfImportTarget
      strategy: RdfImportStrategy
      format: RdfImportFormat
      baseIri?: string
    },
  ) => {
    const fd = new FormData()
    fd.append("file", file)
    fd.append("target", options.target)
    fd.append("strategy", options.strategy)
    fd.append("format", options.format)
    if (options.baseIri?.trim()) fd.append("base_iri", options.baseIri.trim())
    return request<RdfImportResult>(`/api/knowledge/${ksId}/rdf/import`, { method: "POST", body: fd })
  },

  // Controlled terminology (SKOS vocabulary + human-reviewed agent proposals)
  getVocabulary: (ksId: string) => request<VocabularyView>(`/api/knowledge/${ksId}/vocabulary`),
  listVocabularySchemes: (ksId: string) =>
    request<VocabularySchemeList>(`/api/knowledge/${ksId}/vocabulary/schemes`),
  listVocabularyConcepts: (
    ksId: string,
    params: {
      scheme_iri?: string
      q?: string
      status?: "active" | "deprecated"
      mapping?: "mapped" | "standalone"
      origin?: "manual" | "extraction" | "agent"
      start_date?: string
      end_date?: string
      limit?: number
      offset?: number
    } = {},
  ) => {
    const qs = new URLSearchParams()
    if (params.scheme_iri) qs.set("scheme_iri", params.scheme_iri)
    if (params.q) qs.set("q", params.q)
    if (params.status) qs.set("status", params.status)
    if (params.mapping) qs.set("mapping", params.mapping)
    if (params.origin) qs.set("origin", params.origin)
    if (params.start_date) qs.set("start_date", params.start_date)
    if (params.end_date) qs.set("end_date", params.end_date)
    qs.set("limit", String(params.limit ?? 20))
    qs.set("offset", String(params.offset ?? 0))
    return request<VocabularyConceptList>(`/api/knowledge/${ksId}/vocabulary/concepts?${qs.toString()}`)
  },
  syncVocabulary: (ksId: string) => request<{
    scheme_iri: string | null
    terms_added: number
    terms_mapped: number
    aliases_added: number
    broader_added: number
    stale_mappings_removed: number
    mapping_conflicts: number
    view: VocabularyView
  }>(`/api/knowledge/${ksId}/vocabulary/sync`, { method: "POST" }),
  createVocabularyScheme: (
    ksId: string,
    body: { title: string; description: string; default_language: string },
  ) => request<VocabularyScheme>(`/api/knowledge/${ksId}/vocabulary/schemes`, json(body)),
  updateVocabularyScheme: (
    ksId: string,
    iri: string,
    body: { title: string; description: string; default_language: string },
  ) => request<VocabularyScheme>(
    `/api/knowledge/${ksId}/vocabulary/schemes?iri=${encodeURIComponent(iri)}`,
    patch(body),
  ),
  deleteVocabularyScheme: (ksId: string, iri: string) =>
    request<{ deleted: string; removed_triples: number }>(
      `/api/knowledge/${ksId}/vocabulary/schemes?iri=${encodeURIComponent(iri)}`,
      { method: "DELETE" },
    ),
  createVocabularyConcept: (ksId: string, body: VocabularyConceptInput) =>
    request<VocabularyConcept>(`/api/knowledge/${ksId}/vocabulary/concepts`, json(body)),
  updateVocabularyConcept: (ksId: string, iri: string, body: VocabularyConceptInput) =>
    request<VocabularyConcept>(
      `/api/knowledge/${ksId}/vocabulary/concepts?iri=${encodeURIComponent(iri)}`,
      patch(body),
    ),
  deleteVocabularyConcept: (ksId: string, iri: string) =>
    request<{ deleted: string; removed_triples: number }>(
      `/api/knowledge/${ksId}/vocabulary/concepts?iri=${encodeURIComponent(iri)}`,
      { method: "DELETE" },
    ),
  suggestVocabulary: (ksId: string, schemeIri: string) =>
    request<TermProposalList>(
      `/api/knowledge/${ksId}/vocabulary/suggest`,
      json({ scheme_iri: schemeIri }),
    ),
  listTermProposals: (
    ksId: string,
    params: { status?: string; q?: string; limit?: number; offset?: number } = {},
  ) => {
    const qs = new URLSearchParams()
    qs.set("status", params.status ?? "all")
    if (params.q) qs.set("q", params.q)
    qs.set("limit", String(params.limit ?? 100))
    qs.set("offset", String(params.offset ?? 0))
    return request<TermProposalList>(`/api/knowledge/${ksId}/vocabulary/proposals?${qs.toString()}`)
  },
  acceptTermProposal: (ksId: string, proposalId: string, payload?: Record<string, unknown>, note = "") =>
    request<{ proposal: TermProposal; concept: VocabularyConcept }>(
      `/api/knowledge/${ksId}/vocabulary/proposals/${proposalId}/accept`,
      json({ payload, note }),
    ),
  rejectTermProposal: (ksId: string, proposalId: string, note = "") =>
    request<TermProposal>(
      `/api/knowledge/${ksId}/vocabulary/proposals/${proposalId}/reject`,
      json({ note }),
    ),
  exportVocabulary: async (ksId: string, fmt = "turtle"): Promise<string> => {
    const res = await fetch(`/api/knowledge/${ksId}/vocabulary/export?fmt=${fmt}`, { credentials: "include" })
    if (!res.ok) throw new Error(`${res.status}: ${res.statusText}`)
    return res.text()
  },

  // Extraction (starts a background job; poll it for progress)
  runExtraction: (ksId: string, chunkIds: string[], model?: string) =>
    request<ExtractionJob>(`/api/knowledge/${ksId}/extract`, json({ chunk_ids: chunkIds, model })),
  listJobs: (ksId: string) => request<ExtractionJob[]>(`/api/knowledge/${ksId}/jobs`),
  getJob: (ksId: string, jobId: string) =>
    request<ExtractionJob>(`/api/knowledge/${ksId}/jobs/${jobId}`),
  getSources: (ksId: string) => request<SourceDoc[]>(`/api/knowledge/${ksId}/sources`),

  // Change history / audit log
  getHistory: (
    ksId: string,
    params: { category?: string; q?: string; limit?: number; offset?: number } = {},
  ) => {
    const qs = new URLSearchParams()
    if (params.category) qs.set("category", params.category)
    if (params.q) qs.set("q", params.q)
    qs.set("limit", String(params.limit ?? 20))
    qs.set("offset", String(params.offset ?? 0))
    return request<HistoryResponse>(`/api/knowledge/${ksId}/history?${qs.toString()}`)
  },
  rollbackHistory: (ksId: string, eventId: string) =>
    request<{ undone: number; view: OntologyView; open_conflicts: Conflict[] }>(
      `/api/knowledge/${ksId}/history/${eventId}/rollback`,
      { method: "POST" },
    ),

  // Per-knowledge-system model prompts
  listPrompts: (ksId: string) =>
    request<KnowledgePromptList>(`/api/knowledge/${ksId}/prompts`),
  updatePrompt: (ksId: string, promptKey: string, content: string) =>
    request<KnowledgePrompt>(
      `/api/knowledge/${ksId}/prompts/${encodeURIComponent(promptKey)}`,
      put({ content }),
    ),
  restorePrompt: (ksId: string, promptKey: string) =>
    request<KnowledgePrompt>(
      `/api/knowledge/${ksId}/prompts/${encodeURIComponent(promptKey)}`,
      { method: "DELETE" },
    ),
  restoreAllPrompts: (ksId: string) =>
    request<void>(`/api/knowledge/${ksId}/prompts/restore-all`, { method: "POST" }),

  // Manual editing
  editOntology: (ksId: string, op: EditOp) =>
    request<EditResult>(`/api/knowledge/${ksId}/ontology/edit`, json(op)),

  // Conflicts
  detectConflicts: (ksId: string) =>
    request<Conflict[]>(`/api/knowledge/${ksId}/conflicts/detect`, { method: "POST" }),
  listConflicts: (ksId: string, status = "open", ctype?: string) =>
    request<Conflict[]>(`/api/knowledge/${ksId}/conflicts?status=${status}${ctype ? `&ctype=${ctype}` : ""}`),
  getConflictContext: (ksId: string, cid: string) =>
    request<ConflictContext>(`/api/knowledge/${ksId}/conflicts/${cid}`),
  resolveConflict: (ksId: string, cid: string, resolutionId: string) =>
    request<ResolveResult>(
      `/api/knowledge/${ksId}/conflicts/${cid}/resolve`,
      json({ resolution_id: resolutionId }),
    ),
  dismissConflict: (ksId: string, cid: string) =>
    request<Conflict>(`/api/knowledge/${ksId}/conflicts/${cid}/dismiss`, { method: "POST" }),
  reopenConflict: (ksId: string, cid: string) =>
    request<Conflict>(`/api/knowledge/${ksId}/conflicts/${cid}/reopen`, { method: "POST" }),

  // Learned reconciliation memory (TBox domain/range decisions the reconcile agent consults)
  listReconciliations: (ksId: string, params: { q?: string; limit?: number; offset?: number } = {}) => {
    const qs = new URLSearchParams()
    if (params.q) qs.set("q", params.q)
    qs.set("limit", String(params.limit ?? 50))
    qs.set("offset", String(params.offset ?? 0))
    return request<ReconciliationList>(`/api/knowledge/${ksId}/reconciliations?${qs.toString()}`)
  },
  revokeReconciliation: (ksId: string, id: string) =>
    request<{ revoked: number }>(`/api/knowledge/${ksId}/reconciliations/${id}`, { method: "DELETE" }),
  editReconciliationReason: (ksId: string, id: string, reason: string) =>
    request<{ id: string; reason: string }>(`/api/knowledge/${ksId}/reconciliations/${id}`, patch({ reason })),
  revokeResolutionDecision: (ksId: string, id: string) =>
    request<{ revoked: number }>(`/api/knowledge/${ksId}/resolution/decisions/${id}`, { method: "DELETE" }),
  editResolutionReason: (ksId: string, id: string, reason: string) =>
    request<{ id: string; reason: string }>(`/api/knowledge/${ksId}/resolution/decisions/${id}`, patch({ reason })),

  // ABox (instances)
  aboxClasses: (ksId: string) => request<AboxClassList>(`/api/knowledge/${ksId}/abox/classes`),
  aboxIndividuals: (
    ksId: string,
    params: { class_iri?: string; q?: string; limit?: number; offset?: number } = {},
  ) => {
    const qs = new URLSearchParams()
    if (params.class_iri) qs.set("class_iri", params.class_iri)
    if (params.q) qs.set("q", params.q)
    qs.set("limit", String(params.limit ?? 20))
    qs.set("offset", String(params.offset ?? 0))
    return request<IndividualList>(`/api/knowledge/${ksId}/abox/individuals?${qs.toString()}`)
  },
  getIndividual: (ksId: string, iri: string) =>
    request<Individual>(`/api/knowledge/${ksId}/abox/individual?iri=${encodeURIComponent(iri)}`),
  createIndividual: (ksId: string, label: string, classIri: string) =>
    request<Individual>(`/api/knowledge/${ksId}/abox/individuals`, json({ label, class_iri: classIri })),
  deleteIndividual: (ksId: string, iri: string) =>
    request<{ removed: number }>(`/api/knowledge/${ksId}/abox/individuals/delete`, json({ iri })),
  addAssertion: (ksId: string, a: AssertionInput) =>
    request<Individual>(`/api/knowledge/${ksId}/abox/assertions`, json(a)),
  removeAssertion: (ksId: string, a: AssertionInput) =>
    request<Individual>(`/api/knowledge/${ksId}/abox/assertions/delete`, json(a)),

  // ABox instance extraction (background job; poll it like TBox extraction)
  extractInstances: (ksId: string, chunkIds: string[], model?: string) =>
    request<ExtractionJob>(`/api/knowledge/${ksId}/extract-instances`, json({ chunk_ids: chunkIds, model })),
  // One-click schema + instances (TBox then ABox in a single job)
  extractAll: (ksId: string, chunkIds: string[], model?: string) =>
    request<ExtractionJob>(`/api/knowledge/${ksId}/extract-all`, json({ chunk_ids: chunkIds, model })),

  // Entity resolution: manual queue + learned decision log
  getResolutionQueue: (ksId: string, params: { q?: string; limit?: number; offset?: number } = {}) => {
    const qs = new URLSearchParams()
    if (params.q) qs.set("q", params.q)
    qs.set("limit", String(params.limit ?? 50))
    qs.set("offset", String(params.offset ?? 0))
    return request<ResolutionQueue>(`/api/knowledge/${ksId}/resolution/queue?${qs.toString()}`)
  },
  getResolutionDecisions: (ksId: string, params: { q?: string; limit?: number; offset?: number } = {}) => {
    const qs = new URLSearchParams()
    if (params.q) qs.set("q", params.q)
    qs.set("limit", String(params.limit ?? 50))
    qs.set("offset", String(params.offset ?? 0))
    return request<ResolutionDecisions>(`/api/knowledge/${ksId}/resolution/decisions?${qs.toString()}`)
  },
  resolveQueueItem: (ksId: string, resId: string, action: "match" | "new", individualIri?: string) =>
    request<{ id: string; status: string; individual_iri: string | null; summary: string }>(
      `/api/knowledge/${ksId}/resolution/${resId}/resolve`,
      json({ action, individual_iri: individualIri }),
    ),

  // ABox validation (lint individuals against the TBox)
  validateAbox: (ksId: string) => request<ValidationResult>(`/api/knowledge/${ksId}/abox/validate`),
  fixViolation: (ksId: string, op: Record<string, unknown>, summary: string) =>
    request<ValidationResult>(`/api/knowledge/${ksId}/abox/validate/fix`, json({ op, summary })),
  listValidationDecisions: (ksId: string, params: { q?: string; limit?: number; offset?: number } = {}) => {
    const qs = new URLSearchParams()
    if (params.q) qs.set("q", params.q)
    qs.set("limit", String(params.limit ?? 50))
    qs.set("offset", String(params.offset ?? 0))
    return request<ValidationDecisionList>(`/api/knowledge/${ksId}/validation/decisions?${qs.toString()}`)
  },
  revokeValidationDecision: (ksId: string, id: string) =>
    request<{ revoked: number }>(`/api/knowledge/${ksId}/validation/decisions/${id}`, { method: "DELETE" }),
}
