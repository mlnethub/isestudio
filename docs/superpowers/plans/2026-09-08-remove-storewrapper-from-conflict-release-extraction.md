# Remove StoreWrapper From Conflict, Release, and Extraction Services Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Remove production runtime dependencies on `StoreWrapper` from `DuplicateJudge`, `ConflictAgent`, `ConflictService`, `ExtractionOrchestrator`, and `ReleaseManager` while preserving RDF term fidelity, conflict decisions, release snapshots, and extraction rollback.

**Architecture:** PostgreSQL `IRdfStatementRepository` is the authoritative RDF store. Read-only algorithms consume quad snapshots; writes use repository-backed services or `PostgresRdfGraphStore`; release rows remain in PostgreSQL release entities and extraction rollback restores layer snapshots.

**Tech Stack:** .NET 10, EF Core 10, Npgsql, Oxigraph quad/parser types only for in-memory RDF terms.

## Global Constraints

- Do not introduce RocksDB or Oxigraph `Store` runtime creation in migrated services.
- Preserve IRI, blank-node, literal language/datatype, and graph fidelity.
- Do not revert unrelated worktree changes.
- Run `dotnet build src\\ISEStudio\\ISEStudio.csproj --no-restore --verbosity minimal` after each service slice.

### Task 1: DuplicateJudge and ConflictDetection

- Modify `DuplicateJudge.cs` so `DetectAsync` accepts `IReadOnlyList<Oxigraph.Quad>` and calls quad-based `ConflictDetection.ReadClassLabels` and `ReadGraphRelations`.
- Update `ConflictDetection` read helpers to expose quad-list overloads and keep no StoreWrapper reference in the production algorithm path.
- Build the production project.

### Task 2: ConflictAgent and ConflictService

- Inject required `IRdfStatementRepository`.
- Read TBox/ABox snapshots through the repository and use `PostgresRdfGraphStore` for pre/post audit diffs.
- Replace conflict detection and duplicate-judge calls with quad snapshots; remove StoreWrapper constructor parameters and fields.
- Build the production project.

### Task 3: ExtractionOrchestrator

- Replace each phase capture with repository-backed layer snapshot capture.
- On failure restore the affected layer snapshot and preserve failed-job status semantics.
- Update ABox pipeline and duplicate-judge call signatures.
- Build production and migration projects.

### Task 4: ReleaseManager

- Make the PostgreSQL constructor the only constructor.
- Store capture snapshots in `ReleaseStatementEntity` for all three layers, including Vocabulary.
- Read published release statements from PostgreSQL and remove `PublishedEntry`, serving-root, RocksDB materialization, and StoreWrapper disposal paths.
- Build production and migration projects.

### Task 5: Registration and verification

- Remove StoreWrapper parameters from DI registrations and stale comments.
- Scan production source for the five service names and `StoreWrapper` references.
- Build production, migration, and focused tests where available.
