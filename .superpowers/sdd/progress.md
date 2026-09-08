# PostgreSQL Graph Core Execution Ledger

- Merge base: 5d59b9c
- Task 1: complete (commits 5d59b9c..47549b1, review clean with minor scope notes: two necessary ontology namespace-reference updates; supporting contract types required by the declared interface)
- Task 2: complete (commits 47549b1..7615eef, review approved with minor follow-up: Task 5 must add regression assertions for facts confidence/time/timestamptz and the complete new graph foreign-key set)
- Task 3: complete (commits 7615eef..ffc5981, review clean after fixes for evidence knowledge-system isolation, invalidation coverage, and atomic concurrent invalidation)
- Task 4: complete (commits 759f7c7..137aa5d, bidirectional temporal neighborhood query; review approved after fixing independent incoming/outgoing root anchors)
- Task 5: complete (commits 137aa5d..466c549, schema foreign-key/type/index assertions, 1000-fact traversal baseline, architecture boundary documentation; graph tests clean, solution build remains blocked by pre-existing restore/diagnostic issues)

# Ingestion Stage 1

- Document version persistence slice: Stage 1 review fixes complete locally; concurrent winner reload, immutable version/chunk database triggers, composite knowledge-system foreign key, canonical SHA-256 validation, sequential idempotency, and transactional rollback are covered by PostgreSQL integration tests.

# Ingestion Stage 2

- Plain-text application boundary complete: deterministic UTF-8 SHA-256 identity, existing paragraph-aware chunker reuse, ordered chunk metadata persistence, repeated-content idempotency, changed-content versioning, and PostgreSQL integration coverage.
- Stage 2 Unicode review complete: chunk budgets, safe string boundaries, persisted character offsets, and token estimates now use Unicode scalar/code-point semantics; ASCII parity remains covered.
- Acceptance criteria pass; no migration required and no frontend, Rust, parser, connector, job, API, or graph-core behavior changed.

# Ingestion Stage 3

- Plain-text job processor complete (commits e52c499..c0af28d): existing extraction-job lifecycle, DI registration, idempotent version/chunk persistence, failure/retry observability, PostgreSQL integration coverage, and knowledge-system/job-kind ownership guards.

# Ingestion Stage 4

- Parser invocation complete: `DocumentIngestionJobProcessor` reads blobs by
	document SHA, invokes `IDocumentParser`, delegates text to the existing plain
	text service, persists parser metadata, and preserves Stage 3 ownership/kind
	guards.
- PostgreSQL/Testcontainers coverage passes for txt success and Unicode scalar
	metadata, missing blob, unsupported extension, duplicate delivery
	idempotency, and cross-knowledge-system isolation.

# Ingestion Stage 5

- Durable ingestion worker complete: PostgreSQL-backed atomic claiming,
	bounded single-loop hosted dispatch, plain-text payload validation, terminal
	failure handling, and startup recovery for interrupted pending/running jobs.
- Payload is persisted in its own JSONB column and is intentionally separate
	from the immutable `PromptSnapshot`; migration and Testcontainers coverage
	pass. Worker claiming is scoped to `plain_text`, so existing TBox/ABox
	pending rows are left for their own orchestration route.

# Gap Closure Task 1

- Durable handler dispatch for plain_text, tbox, and abox complete. Review fixes
	terminate failed pipeline results and reject replay chunk identities that do
	not match the claimed job. Focused worker/extraction tests and independent
	review passed.

# Gap Closure Task 2

- Multi-format durable ingestion complete and independently approved. HTML, RSS,
	RDF, OWL, PDF, DOCX, XLSX, and plain text preserve the parser/version/chunk
	contract; RDF serialization is content-preserving and deterministic, blob
	bytes are verified against SHA-256, and non-seekable stores are supported.
	Focused parser, processor, worker, workflow, and ingestion suites pass.

# Gap Closure Task 3

- Provider-neutral PostgreSQL search complete and independently approved. FTS
	searches immutable document-version chunks with owner/grant authorization,
	tenant isolation, AsOf/current-version snapshot selection, stable pagination,
	and explicit no-vector capability. Cross-KS document/version corruption is
	rejected at the query boundary; focused unit, PostgreSQL, and persistence
	tests pass.

# Gap Closure Task 4

- Migration rehearsal gates complete and independently reviewed. Fresh,
	restored, and upgrade modes have explicit preconditions, durable JSON
	manifests, before/after SQL and graph evidence, checksum-integrity gates,
	PowerShell validation, and PostgreSQL Testcontainers coverage. Restore and
	production cutover remain externally authorized operations; a real
	`pg_restore` happy path and marker provenance require an environment with
	production-like backup tooling and operator evidence.

# Gap Closure Task 5

- Production-scale graph, search, ingestion, and concurrent-write benchmarks
  complete. The deterministic PostgreSQL 16 Testcontainers fixture uses 10,000
  facts, 5,000 chunks, eight concurrent writers, and depth-4 traversal. The
  runner supports explicit `-Record`, stable result hashes, p95/throughput
  tolerance gates, and nonzero-error rejection. The recorded baseline and a
  subsequent non-record gate both pass; four benchmark tests pass using Docker
  Desktop 29.2.0.

# Gap Closure Task 6

- Production cutover and rollback package complete. Cutover requires explicit
	stop-write confirmation, verified backup and rehearsal manifests, and a smoke
	URL; it records ordered gates and redacts sensitive command output. Rollback
	enforces stop .NET, restore database permissions/backup, unlock Rust, and
	start Rust, retaining backups and resuming from its machine-readable state.
	Container smoke covers health, ingestion, graph, search, and MCP surfaces.
	PowerShell contract/AST checks and focused compilation pass; live container
	execution remains environment-dependent and was skipped when Docker timed out.
