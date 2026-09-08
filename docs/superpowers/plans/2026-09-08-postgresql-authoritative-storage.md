# PostgreSQL Authoritative Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make PostgreSQL the only authoritative runtime store for ISEStudio graph data, replacing Oxigraph/RocksDB-backed runtime paths while preserving RDF import/export and release behavior.

**Architecture:** Follow Utopia's normalized ontology/fact model: entity types, relation types, entities, append-only facts, evidence, recursive PostgreSQL traversal and immutable release rows. Keep RDF as an in-memory boundary format and add PostgreSQL release/provenance projections where exact export or legacy compatibility requires them. Services receive scoped PostgreSQL repositories backed by `ISEStudioDbContext`; no runtime API exposes `Oxigraph.Quad`.

**Tech Stack:** .NET 10, C# 14, EF Core 10, Npgsql 10, PostgreSQL, existing RDF parser/serializer packages only where they do not create a graph store, xUnit integration tests, Docker Compose PostgreSQL.

## Global Constraints

- PostgreSQL is the only authoritative runtime store.
- Oxigraph and RocksDB must not be runtime dependencies of `ISEStudio` or `ISEStudio.Migration`.
- Facts are append-only; invalidation uses `invalidated_at`; corrections use `supersedes_fact_id`.
- All writes for one import/edit/release operation use one PostgreSQL transaction.
- Published release snapshots are immutable PostgreSQL rows and are queried by release id.
- RDF is an import/export boundary format and is never persisted through a fallback Oxigraph store.
- Preserve the unrelated user change in `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs`.
- Do not change public API behavior unless the old behavior depended on Oxigraph/RocksDB storage.

---

### Task 1: Establish PostgreSQL graph schema and EF model

**Files:**
- Create: `src/ISEStudio/Infrastructure/Persistence/Migrations/20260908034907_PostgresAuthoritativeGraph.cs`
- Create: `src/ISEStudio/Infrastructure/Persistence/Migrations/20260908034907_PostgresAuthoritativeGraph.Designer.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Entities/GraphEntities.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Entities/ReleaseEntities.cs`
- Create: `src/ISEStudio/Infrastructure/Persistence/Entities/OntologyAxiomEntities.cs`
- Create: `src/ISEStudio/Infrastructure/Persistence/Entities/ReleaseStatementEntity.cs`
- Create: `src/ISEStudio/Infrastructure/Persistence/Configurations/AuthoritativeGraphConfiguration.cs`
- Create: `src/ISEStudio/Infrastructure/Persistence/Configurations/ReleaseStatementConfiguration.cs`
- Test: `src/ISEStudio.Tests/Infrastructure/Persistence/AuthoritativeGraphSchemaTests.cs`

**Interfaces:**
- Produces `EntityTypeEntity`, `RelationTypeEntity`, `GraphEntityEntity`, `FactEntity`, `FactEvidenceEntity`, `OntologyAxiomEntity` and `ReleaseStatementEntity` mappings usable by later repositories.
- Adds `DbSet<T>` properties and indexes for `(KnowledgeSystemId, Iri)`, `(SubjectId, RelationTypeId, ValidFrom)`, `(ReleaseId, StatementHash)` and active-fact queries.

- [ ] **Step 1: Write schema assertions** for required tables, foreign keys, unique keys, append-only fact columns, release immutability columns and recursive traversal indexes.
- [ ] **Step 2: Run the PostgreSQL schema test** against the repository's test database and verify it fails because the new migration/entities are absent.
- [ ] **Step 3: Add the migration and EF mappings** with explicit PostgreSQL types for JSONB, timestamps, UUIDs, bytea and vector fields already supported by the project.
- [ ] **Step 4: Run the schema test** and verify all required tables, constraints and indexes exist.
- [ ] **Step 5: Run `dotnet build src/ISEStudio/ISEStudio.csproj`** and resolve only schema/model compilation errors.

### Task 2: Implement the PostgreSQL graph repository

**Files:**
- Create: `src/ISEStudio/Infrastructure/Persistence/Repositories/IPostgresGraphRepository.cs`
- Create: `src/ISEStudio/Infrastructure/Persistence/Repositories/PostgresGraphRepository.cs`
- Create: `src/ISEStudio/Infrastructure/Persistence/Repositories/GraphQueryModels.cs`
- Modify: `src/ISEStudio/Graph/GraphStore.cs`
- Test: `src/ISEStudio.Tests/Infrastructure/Persistence/PostgresGraphRepositoryTests.cs`

**Interfaces:**
- `Task<EntityTypeEntity> CreateEntityTypeAsync(...)`
- `Task<RelationTypeEntity> CreateRelationTypeAsync(...)`
- `Task<GraphEntityEntity> CreateEntityAsync(...)`
- `Task<FactEntity> RecordFactAsync(...)`
- `Task InvalidateFactAsync(Guid factId, DateTimeOffset at, CancellationToken cancellationToken)`
- `Task<IReadOnlyList<NeighborhoodNode>> GetNeighborhoodAsync(Guid knowledgeSystemId, Guid rootEntityId, int depth, CancellationToken cancellationToken)`
- `Task<IReadOnlyList<FactQueryRow>> QueryFactsAsync(...)`

- [ ] **Step 1: Write failing tests** for entity creation, relation creation, append-only fact insertion, invalidation, evidence association and depth-limited recursive neighborhood traversal.
- [ ] **Step 2: Run only those tests** and verify failures identify missing repository methods rather than database connectivity problems.
- [ ] **Step 3: Implement repository methods** using one `DbContext` transaction per public mutation and a parameterized recursive CTE for neighborhoods.
- [ ] **Step 4: Update `GraphStore`** to delegate PostgreSQL fact and neighborhood operations without changing its externally used result contracts.
- [ ] **Step 5: Rerun repository and GraphStore tests**, then run the focused project test filter.

### Task 3: Replace ontology editing and ABox mutations

**Files:**
- Modify: `src/ISEStudio/Ontology/OntologyEditor.cs`
- Modify: `src/ISEStudio/Ontology/ABoxManager.cs`
- Modify: `src/ISEStudio/Conflicts/ConflictServiceCollectionExtensions.cs`
- Create or modify: `src/ISEStudio/Ontology/IOntologyRepository.cs`
- Test: `src/ISEStudio.Tests/Ontology/OntologyEditorPostgresTests.cs`
- Test: `src/ISEStudio.Tests/Ontology/ABoxManagerPostgresTests.cs`

**Interfaces:**
- Ontology edits use repository transactions for class/relation/domain/range/parent/disjoint/axiom writes.
- ABox mutations use `CreateEntityAsync`, `RecordFactAsync`, `InvalidateFactAsync` and evidence methods.
- Existing service methods retain their request/response types; only their storage implementation changes.

- [ ] **Step 1: Add failing tests** covering class creation, relation domain/range, subclass cycle rejection, ABox entity creation, assertion correction and rollback after a validation failure.
- [ ] **Step 2: Run the focused tests** and confirm they fail on existing StoreWrapper/Oxigraph paths.
- [ ] **Step 3: Replace direct StoreWrapper calls** with repository operations inside scoped PostgreSQL transactions.
- [ ] **Step 4: Verify SQLite contract tests use test doubles or explicit test-only adapters** and are not registered as production storage.
- [ ] **Step 5: Run ontology and ABox test filters** and `dotnet build src/ISEStudio/ISEStudio.csproj`.

### Task 4: Move RDF import and export to PostgreSQL projections

**Files:**
- Modify: `src/ISEStudio/Ontology/RdfImportService.cs`
- Modify: `src/ISEStudio/Ontology/RdfExportService.cs`
- Create: `src/ISEStudio/Ontology/RdfPostgresProjection.cs`
- Create: `src/ISEStudio/Ontology/RdfTermMapper.cs`
- Modify: `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs`
- Test: `src/ISEStudio.Tests/Ontology/RdfPostgresRoundTripTests.cs`
- Test: `src/ISEStudio.Tests/Ontology/RdfImportAtomicityTests.cs`

**Interfaces:**
- `RdfPostgresProjection.ImportAsync(RdfDocument document, ImportContext context, CancellationToken cancellationToken)`.
- `RdfPostgresProjection.ExportAsync(ExportContext context, CancellationToken cancellationToken)`.
- `RdfTermMapper` maps URI, blank-node, language-tagged literal, typed literal and named-graph terms to PostgreSQL-backed domain values.

- [ ] **Step 1: Write failing round-trip tests** for TBox, ABox, named graph, blank node, language-tagged literal and typed literal cases.
- [ ] **Step 2: Write the atomicity test** that injects a failing statement and asserts no partial entity, relation or fact rows remain.
- [ ] **Step 3: Implement in-memory parse plus one PostgreSQL transaction** for TBox/ABox projection; unsupported statements must be recorded or rejected by policy.
- [ ] **Step 4: Implement PostgreSQL read projection and existing serializer adapters** without opening a local store.
- [ ] **Step 5: Run round-trip and atomicity tests**, then scan the service files to ensure no `StoreWrapper` or Oxigraph type remains.

### Task 5: Replace release capture, publication and serving reads

**Files:**
- Modify: `src/ISEStudio/Ontology/ReleaseManager.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Entities/ReleaseEntities.cs`
- Modify: `src/ISEStudio/Ontology/ReleaseArtifactStore.cs`
- Create: `src/ISEStudio/Ontology/PostgresReleaseSnapshotRepository.cs`
- Test: `src/ISEStudio.Tests/Ontology/PostgresReleaseManagerTests.cs`

**Interfaces:**
- `Task<Guid> CaptureAsync(Guid knowledgeSystemId, string version, CancellationToken cancellationToken)` creates immutable statement rows.
- `Task PublishAsync(Guid releaseId, CancellationToken cancellationToken)` records deployment state without creating a filesystem/RocksDB serving store.
- `Task<IReadOnlyList<ReleaseStatementRow>> QueryPublishedAsync(Guid releaseId, ReleaseQuery query, CancellationToken cancellationToken)` reads only release-scoped rows.

- [ ] **Step 1: Write failing tests** for capture, publish, release-scoped read isolation, duplicate publication rejection and deletion.
- [ ] **Step 2: Run the focused release tests** and verify current filesystem/RocksDB behavior fails the PostgreSQL isolation assertions.
- [ ] **Step 3: Implement immutable release statement persistence** with statement hashes and release foreign keys; reject updates after publication.
- [ ] **Step 4: Replace `ReleaseArtifactStore` serving behavior** with PostgreSQL metadata/export artifact behavior only.
- [ ] **Step 5: Rerun release tests** and scan for `OpenReadOnly`, `serving/` and RocksDB construction.

### Task 6: Replace query/SPARQL and DI runtime boundaries

**Files:**
- Modify: `src/ISEStudio/Program.cs`
- Modify: `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs`
- Modify: `src/ISEStudio/Ontology/OntologyService.cs`
- Modify: `src/ISEStudio/Ontology/OntologyViewBuilder.cs`
- Modify: `src/ISEStudio/Ontology/PublishedDataService.cs`
- Modify: `src/ISEStudio/Ontology/PublishedOntologyService.cs`
- Modify: `src/ISEStudio/Sparql/SparqlQueryExecutor.cs`
- Modify: `src/ISEStudio/Sparql/SparqlQueryExecutorServiceCollectionExtensions.cs`
- Modify: `src/ISEStudio/Conflicts/ConflictAgent.cs`
- Modify: `src/ISEStudio/Conflicts/ConflictService.cs`
- Modify: `src/ISEStudio/EntityResolution/ResolutionService.cs`
- Modify: `src/ISEStudio/Exports/ExportRunner.cs`
- Modify: `src/ISEStudio/Exports/ExportServiceCollectionExtensions.cs`
- Modify: `src/ISEStudio/Extraction/CorpusRecoveryService.cs`
- Modify: `src/ISEStudio/Extraction/ExtractionMerger.cs`
- Modify: `src/ISEStudio/Extraction/ExtractionOrchestrator.cs`
- Modify: `src/ISEStudio/Extraction/IExtractionMerger.cs`
- Modify: `src/ISEStudio/Extraction/TerminologyService.cs`
- Modify: `src/ISEStudio/Integration/InternalOperationDispatcher.cs`
- Modify: `src/ISEStudio/Knowledge/KnowledgeService.cs`
- Modify: `src/ISEStudio/Knowledge/KnowledgeStatsService.cs`
- Modify: `src/ISEStudio/Observability/Telemetry.cs`
- Modify: `src/ISEStudio/Ontology/ABoxService.cs`
- Modify: `src/ISEStudio/Ontology/ABoxValidator.cs`
- Modify: `src/ISEStudio/Ontology/ConflictDetection.cs`
- Modify: `src/ISEStudio/Ontology/ConflictDetector.cs`
- Modify: `src/ISEStudio/Ontology/DuplicateJudge.cs`
- Modify: `src/ISEStudio/Ontology/ExternalApiService.cs`
- Modify: `src/ISEStudio/Ontology/ExternalOntologyService.cs`
- Modify: `src/ISEStudio/Ontology/HistoryService.cs`
- Modify: `src/ISEStudio/Ontology/NQuadsTermWriter.cs`
- Modify: `src/ISEStudio/Ontology/ReleaseManifest.cs`
- Modify: `src/ISEStudio/Ontology/ReleaseService.cs`
- Modify: `src/ISEStudio/Ontology/SchemaBuilder.cs`
- Modify: `src/ISEStudio/Ontology/ShaclValidator.cs`
- Modify: `src/ISEStudio/Ontology/SkosManager.cs`
- Modify: `src/ISEStudio/Ontology/StatementProvenanceService.cs`
- Modify: `src/ISEStudio/Ontology/StructureAgent.cs`
- Modify: `src/ISEStudio/Ontology/VocabularyProposalService.cs`
- Modify: `src/ISEStudio/Ontology/VocabularyService.cs`
- Modify: `src/ISEStudio/Program.cs`
- Modify: `src/ISEStudio/Sparql/SparqlQueryExecutor.cs`
- Create: `src/ISEStudio/Ontology/PostgresOntologyQueryService.cs`
- Create: `src/ISEStudio/Ontology/PostgresPublishedQueryService.cs`
- Test: `src/ISEStudio.Tests/Infrastructure/Queries/PostgresQueryServiceTests.cs`

**Interfaces:**
- Query services expose the existing application DTOs and accept scoped repository/context dependencies.
- Production DI registers PostgreSQL repositories as scoped services; no singleton mutable graph store is registered.

- [ ] **Step 1: Add failing query tests** for ontology views, entity instances, facts, neighborhoods, provenance and published release reads.
- [ ] **Step 2: Implement PostgreSQL query services** with explicit parameterized queries and bounded traversal depth.
- [ ] **Step 3: Replace direct StoreWrapper/SPARQL injections** and register scoped services in `Program.cs`.
- [ ] **Step 4: Run API/query tests** and verify application startup does not create `data/rdf` or serving directories.
- [ ] **Step 5: Run `dotnet test src/ISEStudio.Tests`** with the PostgreSQL test profile.

### Task 7: Implement legacy data cutover in the migration project

**Files:**
- Modify: `src/ISEStudio.Migration/Program.cs`
- Modify: `src/ISEStudio.Migration/ISEStudio.Migration.csproj`
- Create: `src/ISEStudio.Migration/LegacyGraphReader.cs`
- Create: `src/ISEStudio.Migration/PostgresCutoverWriter.cs`
- Create: `src/ISEStudio.Migration/CutoverVerifier.cs`
- Test: `src/ISEStudio.Migration.Tests/LegacyCutoverTests.cs`

**Interfaces:**
- `LegacyGraphReader` reads legacy input once and yields normalized statements.
- `PostgresCutoverWriter` writes statements using the production PostgreSQL projection transaction.
- `CutoverVerifier` compares counts and per-knowledge-system SHA-256 statement checksums and returns a nonzero failure result on mismatch.

- [ ] **Step 1: Write failing tests** for blank nodes, named graphs, unsupported statement reporting, checksum equality and rollback on mismatch.
- [ ] **Step 2: Implement the one-time input reader** using only the migration tool's conversion boundary; it must not be referenced by production DI.
- [ ] **Step 3: Implement PostgreSQL writing and verification** with fail-closed behavior.
- [ ] **Step 4: Run migration tests** against a disposable PostgreSQL database and verify reruns are idempotent or explicitly rejected.
- [ ] **Step 5: Remove Oxigraph runtime references from the migration project** while retaining only the minimal input conversion dependency if it is required and non-runtime.

### Task 8: Remove runtime dependencies and perform final validation

**Files:**
- Modify: `src/ISEStudio/ISEStudio.csproj`
- Modify: `src/ISEStudio.Migration/ISEStudio.Migration.csproj`
- Modify: `src/ISEStudio.sln` to remove `ISEStudio.OxigraphProbe` from the production solution
- Retain: `src/ISEStudio.OxigraphProbe/` as an explicitly offline historical conversion tool, excluded from production and migration builds
- Modify: `Directory.Build.props` or `Directory.Build.targets` only if they contain Oxigraph/RocksDB package references
- Test: `scripts/verify-postgresql-authoritative-storage.ps1`

- [ ] **Step 1: Add a verification script** that scans project files, source and startup configuration for Oxigraph packages, RocksDB paths, runtime Store construction and Oxigraph DI registration.
- [ ] **Step 2: Remove production and migration package references** and compile-time using aliases after all service paths are PostgreSQL-backed.
- [ ] **Step 3: Run the verification script** and expect zero production/migration runtime matches.
- [ ] **Step 4: Run `dotnet restore`, focused PostgreSQL integration tests, all solution tests and `dotnet build`** for production and migration projects.
- [ ] **Step 5: Run a startup smoke test with legacy RDF directories absent** and confirm PostgreSQL-backed ontology, ABox, release and export operations work.

## Self-Review Checklist

- [ ] Every spec section maps to at least one task: schema, normalized facts, PostgreSQL repository, RDF import/export, release snapshots, query replacement, legacy cutover, dependency removal and acceptance tests.
- [ ] No task requires production startup to access Oxigraph or RocksDB.
- [ ] Repository method names used by later tasks are defined in Task 2.
- [ ] Release method names used by later tasks are defined in Task 5.
- [ ] The migration boundary is isolated to `ISEStudio.Migration` and is not part of production DI.
- [ ] The existing Dovetail worktree change is not included in any edit or cleanup step.
