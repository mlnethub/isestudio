# PostgreSQL Runtime Cutover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans (strongly recommended). Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the PostgreSQL-authoritative runtime cutover for ontology editing, ABox operations, RDF import/export, releases, query services, migration cutover, and production dependency removal.

**Architecture:** PostgreSQL is the only runtime source of truth. The normalized model uses `entity_types`, `relation_types`, link tables, `graph_entities`, append-only `facts`, evidence, axioms, and immutable release statements. RDF remains an in-memory boundary: dotNetRDF parses and serializes terms/documents, while PostgreSQL projection code performs all live reads and writes; no runtime API exposes `Oxigraph.Quad`.

**Tech Stack:** .NET 10, EF Core 10, Npgsql 10.0.3, PostgreSQL 16 Testcontainers, xUnit, dotNetRDF 3.5.2 only for parser/writer APIs, existing ASP.NET Core DI and application DTO contracts.

## Global Constraints

- PostgreSQL is the only authoritative runtime store.
- Oxigraph and RocksDB must not be runtime dependencies of `ISEStudio` or `ISEStudio.Migration`.
- Facts are append-only; invalidation uses `invalidated_at`; corrections use `supersedes_fact_id`.
- All writes affecting one operation use one PostgreSQL transaction.
- Published release snapshots are immutable PostgreSQL rows and are queried by release id.
- RDF is an import/export boundary format and is never persisted through a fallback Oxigraph store.
- Unsupported RDF statements are recorded in PostgreSQL import/provenance payloads or rejected by an explicit policy; they are never silently written to another store.
- Production startup must not create `data/rdf`, `serving/{releaseId}`, or nullable Oxigraph services.
- `Oxigraph.Quad` must not appear in PostgreSQL repository or production service APIs.
- Preserve `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` exactly as an unrelated user change.
- SQLite remains test-only and must not define production storage behavior.
- Do not commit changes; each task ends with a proposed commit command for the implementer.

---

## File Map Before Implementation

**PostgreSQL graph boundary:**
- `src/ISEStudio/Infrastructure/Persistence/Repositories/IPostgresGraphRepository.cs` owns type, relation, entity, fact, evidence, axiom, and query contracts.
- `src/ISEStudio/Infrastructure/Persistence/Repositories/PostgresGraphRepository.cs` implements those contracts with EF transactions and parameterized Npgsql SQL for recursive queries.
- `src/ISEStudio/Graph/GraphStore.cs` remains the compatibility facade for existing graph DTOs and delegates to the repository.

**Ontology and ABox services:**
- `src/ISEStudio/Ontology/IOntologyRepository.cs` owns TBox edits and PostgreSQL transaction boundaries.
- `src/ISEStudio/Ontology/PostgresOntologyRepository.cs` implements class/property/domain/range/parent/disjoint/axiom operations.
- `src/ISEStudio/Ontology/OntologyEditor.cs` keeps operation validation and dispatch but has no `StoreWrapper` field.
- `src/ISEStudio/Ontology/ABoxManager.cs` keeps public request/response methods but delegates individuals and assertions to graph repository operations.

**RDF boundary:**
- `src/ISEStudio/Ontology/RdfTerm.cs` represents URI, blank-node, plain literal, language literal, and typed literal without Oxigraph types.
- `src/ISEStudio/Ontology/RdfStatement.cs` represents subject, predicate, object, and optional graph IRI.
- `src/ISEStudio/Ontology/RdfImportParser.cs` returns `RdfStatement` values from dotNetRDF parser output.
- `src/ISEStudio/Ontology/RdfPostgresProjection.cs` imports/exports PostgreSQL statements in one transaction.
- `src/ISEStudio/Ontology/RdfExportService.cs` serializes projection output in memory with dotNetRDF writers and existing term writers.

**Release boundary:**
- `src/ISEStudio/Ontology/PostgresReleaseSnapshotRepository.cs` owns immutable release statement capture, publication state, scoped reads, and deletion rules.
- `src/ISEStudio/Ontology/ReleaseManager.cs` preserves public lifecycle DTOs but has no artifact path, serving directory, or `StoreWrapper`.
- `src/ISEStudio/Ontology/ReleaseArtifactStore.cs` is deleted or reduced to an explicitly non-runtime export artifact helper only after all consumers move to PostgreSQL.

**Queries and DI:**
- `src/ISEStudio/Ontology/PostgresOntologyQueryService.cs` serves live ontology/entity/fact/provenance DTOs.
- `src/ISEStudio/Ontology/PostgresPublishedQueryService.cs` serves release-scoped rows.
- `src/ISEStudio/Sparql/PostgresSparqlQueryExecutor.cs` implements the documented bounded SPARQL subset or returns a validation error for unsupported constructs.
- `src/ISEStudio/Program.cs` and `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs` register scoped PostgreSQL services only.

**Migration and verification:**
- `src/ISEStudio.Migration/LegacyGraphReader.cs` is the only legacy-store input adapter.
- `src/ISEStudio.Migration/PostgresCutoverWriter.cs` writes normalized PostgreSQL rows.
- `src/ISEStudio.Migration/CutoverVerifier.cs` verifies counts and SHA-256 checksums.
- `scripts/verify-postgresql-authoritative-storage.ps1` fails on runtime Oxigraph/RocksDB references in production or migration projects.

---

### Task 1: Complete the PostgreSQL graph repository contract

**Files:**
- Modify: `src/ISEStudio/Infrastructure/Persistence/Repositories/IPostgresGraphRepository.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Repositories/PostgresGraphRepository.cs`
- Create: `src/ISEStudio/Infrastructure/Persistence/Repositories/GraphQueryModels.cs`
- Modify: `src/ISEStudio/Graph/GraphStore.cs`
- Test: `src/ISEStudio.IntegrationTests/Graph/GraphStoreTests.cs`
- Test: `src/ISEStudio.Tests/Infrastructure/Persistence/PostgresGraphRepositoryTests.cs`

**Interfaces:**
- `Task<EntityTypeEntity> CreateEntityTypeAsync(Guid knowledgeSystemId, string iri, string key, string? label, string? description, Guid? actorId, CancellationToken cancellationToken)`
- `Task<RelationTypeEntity> CreateRelationTypeAsync(Guid knowledgeSystemId, string iri, string key, string? label, string? description, Guid? actorId, CancellationToken cancellationToken)`
- `Task<GraphEntity> CreateEntityAsync(CreateGraphEntityCommand command, CancellationToken cancellationToken)`
- `Task<GraphFact> RecordFactAsync(RecordFactCommand command, CancellationToken cancellationToken)`
- `Task InvalidateFactAsync(Guid knowledgeSystemId, Guid factId, DateTimeOffset invalidatedAt, CancellationToken cancellationToken)`
- `Task<GraphNeighborhood> GetNeighborhoodAsync(GraphNeighborhoodQuery query, CancellationToken cancellationToken)`
- `Task<IReadOnlyList<FactQueryRow>> QueryFactsAsync(Guid knowledgeSystemId, FactQuery query, CancellationToken cancellationToken)`

- [ ] **Step 1: Write repository tests for missing operations.** Add tests that call `CreateEntityTypeAsync`, `CreateRelationTypeAsync`, `RecordFactAsync`, `InvalidateFactAsync`, and `QueryFactsAsync`; assert returned rows, audit actions, evidence count, and cross-KS rejection.
- [ ] **Step 2: Run the focused tests and record the failures.**

  ```powershell
  dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~PostgresGraphRepository --no-restore
  ```

  Expected: failures identify unimplemented repository methods, not Docker or migration connectivity errors.
- [ ] **Step 3: Add exact query/result records.** Define `FactQuery`, `FactQueryRow`, `NeighborhoodNode`, and `NeighborhoodFactRow` in `GraphQueryModels.cs`; keep all properties in terms of `Guid`, strings, nullable values, and existing graph DTOs, never Oxigraph terms.
- [ ] **Step 4: Implement type and relation creation.** Validate the knowledge system, enforce `(knowledge_system_id, iri)` uniqueness, write the row and `graph.entity_type.created` or `graph.relation_type.created` audit event inside one transaction.
- [ ] **Step 5: Move fact insertion and invalidation behind the repository.** Preserve append-only insertion, evidence ownership checks, `InvalidatedAt`, `SupersedesFactId`, and the existing audit actions.
- [ ] **Step 6: Move neighborhood and fact queries into the repository.** Use the existing recursive CTE from `GraphStore.GetNeighborhoodAsync`; bind every parameter with Npgsql and enforce `MaxDepth <= 10_000` before execution.
- [ ] **Step 7: Delegate `GraphStore` methods to the completed repository.** Preserve `GraphStore(ISEStudioDbContext)` for direct test construction and use DI injection in production.
- [ ] **Step 8: Run repository and Graph tests.**

  ```powershell
  dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~ISEStudio.IntegrationTests.Graph --no-restore
  dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~PostgresGraphRepository --no-restore
  ```

  Expected: all repository and Graph tests pass.
- [ ] **Step 9: Commit the independently testable graph repository slice.**

  ```powershell
  git add src/ISEStudio/Infrastructure/Persistence/Repositories src/ISEStudio/Graph/GraphStore.cs src/ISEStudio.Tests/Infrastructure/Persistence src/ISEStudio.IntegrationTests/Graph
  git commit -m "feat: complete postgres graph repository"
  ```

---

### Task 2: Replace OntologyEditor with transactional PostgreSQL TBox edits

**Files:**
- Create: `src/ISEStudio/Ontology/IOntologyRepository.cs`
- Create: `src/ISEStudio/Ontology/PostgresOntologyRepository.cs`
- Modify: `src/ISEStudio/Ontology/OntologyEditor.cs`
- Modify: `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs`
- Test: `src/ISEStudio.Tests/Ontology/OntologyEditorPostgresTests.cs`

**Interfaces:**
- `Task<string> AddClassAsync(Guid knowledgeSystemId, string baseIri, string label, string? description, Guid? actorId, CancellationToken cancellationToken)`
- `Task UpdateClassAsync(Guid knowledgeSystemId, string iri, string? label, string? description, Guid? actorId, CancellationToken cancellationToken)`
- `Task DeleteClassAsync(Guid knowledgeSystemId, string iri, Guid? actorId, CancellationToken cancellationToken)`
- `Task<string> AddPropertyAsync(Guid knowledgeSystemId, string baseIri, string label, string? description, Guid? actorId, CancellationToken cancellationToken)`
- `Task UpdatePropertyAsync(Guid knowledgeSystemId, string iri, string? label, string? description, Guid? actorId, CancellationToken cancellationToken)`
- `Task DeletePropertyAsync(Guid knowledgeSystemId, string iri, Guid? actorId, CancellationToken cancellationToken)`
- `Task SetPropertyUnionAsync(Guid knowledgeSystemId, string propertyIri, IReadOnlyList<string> memberIris, Guid? actorId, CancellationToken cancellationToken)`
- `Task AddAxiomAsync(Guid knowledgeSystemId, OntologyAxiomInput axiom, Guid? actorId, CancellationToken cancellationToken)`
- `Task DeleteAxiomAsync(Guid knowledgeSystemId, OntologyAxiomInput axiom, Guid? actorId, CancellationToken cancellationToken)`
- `Task MergePropertiesAsync(Guid knowledgeSystemId, IReadOnlyList<string> sources, string target, Guid? actorId, CancellationToken cancellationToken)`
- `Task MergeClassesAsync(Guid knowledgeSystemId, string source, string target, Guid? actorId, CancellationToken cancellationToken)`

- [ ] **Step 1: Add failing PostgreSQL tests.** Cover class create/update/delete, property create/update/delete, domain/range rows, parent rows, axiom payloads, property merge, class merge, and rollback when a referenced IRI is missing.
- [ ] **Step 2: Run the focused tests before changing the service.**

  ```powershell
  dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~OntologyEditorPostgres --no-restore
  ```

  Expected: failures show `OntologyEditor` still requires `StoreWrapper` or the repository methods are absent.
- [ ] **Step 3: Implement `PostgresOntologyRepository` transaction helpers.** Add `BeginTransactionAsync`, `RequireKnowledgeSystemAsync`, `RequireEntityTypeAsync`, `RequireRelationTypeAsync`, and `WriteAuditAsync`; ensure every public mutation calls `SaveChangesAsync` and commits exactly once.
- [ ] **Step 4: Implement class and property CRUD.** Generate IRIs from `BaseIri`, map class rows to `EntityTypeEntity`, map properties to `RelationTypeEntity`, and preserve labels/descriptions and audit action names.
- [ ] **Step 5: Implement relation domain/range, parent, disjoint, and axiom writes.** Use link-table rows for supported dedicated relationships and `OntologyAxiomEntity.Payload` for remaining axiom types; reject cross-KS references.
- [ ] **Step 6: Implement cycle validation in PostgreSQL.** Before adding a parent edge, run a recursive CTE from the proposed child and reject a path that reaches the child; roll back the transaction on rejection.
- [ ] **Step 7: Remove `StoreWrapper` from `OntologyEditor`.** Keep payload validation and operation dispatch in `ApplyEditAsync`, replace capture/revert with the repository transaction, and retain the null-store contract-test behavior only through an explicit test double, not a nullable production dependency.
- [ ] **Step 8: Register `IOntologyRepository` as scoped.** Do not register `OntologyEditor`, `StoreWrapper`, or any mutable graph store as a singleton.
- [ ] **Step 9: Run ontology tests and build.**

  ```powershell
  dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~OntologyEditor --no-restore
  dotnet build src\ISEStudio\ISEStudio.csproj --no-restore --verbosity quiet
  ```

  Expected: focused tests pass and the production project builds.
- [ ] **Step 10: Commit the ontology editor slice.**

  ```powershell
  git add src/ISEStudio/Ontology/IOntologyRepository.cs src/ISEStudio/Ontology/PostgresOntologyRepository.cs src/ISEStudio/Ontology/OntologyEditor.cs src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs src/ISEStudio.Tests/Ontology/OntologyEditorPostgresTests.cs
  git commit -m "feat: move ontology edits to postgres"
  ```

---

### Task 3: Replace ABoxManager and assertion mutations

**Files:**
- Modify: `src/ISEStudio/Ontology/ABoxManager.cs`
- Modify: `src/ISEStudio/Ontology/ABoxService.cs`
- Modify: `src/ISEStudio/Ontology/ABoxValidator.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Repositories/IPostgresGraphRepository.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Repositories/PostgresGraphRepository.cs`
- Test: `src/ISEStudio.Tests/Ontology/ABoxManagerPostgresTests.cs`
- Test: `src/ISEStudio.IntegrationTests/Graph/ABoxPostgresTests.cs`

**Interfaces:**
- `Task<string> CreateIndividualAsync(KsContext ks, string classIri, string label, Guid? actorId, CancellationToken cancellationToken)`
- `Task DeleteIndividualAsync(Guid knowledgeSystemId, string individualIri, Guid? actorId, CancellationToken cancellationToken)`
- `Task<bool> AddObjectAssertionAsync(Guid knowledgeSystemId, string subjectIri, string propertyIri, string targetIri, Guid? actorId, CancellationToken cancellationToken)`
- `Task<bool> AddDataAssertionAsync(Guid knowledgeSystemId, string subjectIri, string propertyIri, RdfLiteralValue value, Guid? actorId, CancellationToken cancellationToken)`
- `Task InvalidateAssertionAsync(Guid knowledgeSystemId, Guid factId, DateTimeOffset at, Guid? actorId, CancellationToken cancellationToken)`
- `Task<IReadOnlyList<ABoxAssertion>> QueryAssertionsAsync(Guid knowledgeSystemId, string? subjectIri, string? propertyIri, CancellationToken cancellationToken)`

- [ ] **Step 1: Add failing tests for ABox persistence.** Assert individual creation creates a `graph_entities` row, `rdf:type` and label facts, object/data assertions are append-only, duplicate assertions return `false`, and invalidation writes audit without deleting history.
- [ ] **Step 2: Add rollback and cascade tests.** Force an unknown class/property or cross-KS target and assert no graph entity, fact, evidence, or audit row remains; verify deleting an individual invalidates its facts rather than physically deleting append-only history.
- [ ] **Step 3: Implement ABox repository methods.** Resolve subject/object IRIs to graph entity IDs, resolve property IRIs to relation type IDs, store literal values in the existing fact object-value JSON/text shape, and write audit rows in the same transaction.
- [ ] **Step 4: Replace synchronous StoreWrapper operations in `ABoxManager`.** Preserve existing public overloads by adding async repository-backed implementations and thin compatibility wrappers only where current callers require synchronous return types.
- [ ] **Step 5: Update `ABoxService` and validator reads.** Replace `Match` scans with repository queries; keep validation rules and wire DTOs unchanged.
- [ ] **Step 6: Register the repository-backed ABox services as scoped.** Remove nullable StoreWrapper constructor paths from production registrations; use test-only fakes in SQLite contract tests.
- [ ] **Step 7: Run focused ABox and Graph tests.**

  ```powershell
  dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~ABox --no-restore
  dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~ABoxPostgres --no-restore
  ```

  Expected: all ABox tests pass and no test reports a StoreWrapper/Oxigraph dependency.
- [ ] **Step 8: Commit the ABox slice.**

  ```powershell
  git add src/ISEStudio/Ontology/ABoxManager.cs src/ISEStudio/Ontology/ABoxService.cs src/ISEStudio/Ontology/ABoxValidator.cs src/ISEStudio/Infrastructure/Persistence/Repositories src/ISEStudio.Tests/Ontology/ABoxManagerPostgresTests.cs src/ISEStudio.IntegrationTests/Graph/ABoxPostgresTests.cs
  git commit -m "feat: move abox mutations to postgres"
  ```

---

### Task 4: Introduce Oxigraph-free RDF terms and PostgreSQL projection

**Files:**
- Create: `src/ISEStudio/Ontology/RdfTerm.cs`
- Create: `src/ISEStudio/Ontology/RdfStatement.cs`
- Modify: `src/ISEStudio/Ontology/RdfImportParser.cs`
- Create: `src/ISEStudio/Ontology/RdfTermMapper.cs`
- Create: `src/ISEStudio/Ontology/RdfPostgresProjection.cs`
- Modify: `src/ISEStudio/Ontology/RdfImportService.cs`
- Test: `src/ISEStudio.Tests/Ontology/RdfPostgresRoundTripTests.cs`
- Test: `src/ISEStudio.Tests/Ontology/RdfImportAtomicityTests.cs`

**Interfaces:**
- `abstract record RdfTerm`
- `sealed record RdfIri(string Value) : RdfTerm`
- `sealed record RdfBlankNode(string Id) : RdfTerm`
- `sealed record RdfLiteral(string Value, string? Language, string? Datatype) : RdfTerm`
- `sealed record RdfStatement(RdfTerm Subject, string PredicateIri, RdfTerm Object, string? GraphIri)`
- `ParsedRdfDocument Parse(byte[] data, string filename, string requestedFormat, string? baseIri, int? maxTriples, string blankNodeScope)`
- `Task<RdfProjectionResult> ImportAsync(IReadOnlyList<RdfStatement> statements, RdfImportContext context, CancellationToken cancellationToken)`
- `Task<IReadOnlyList<RdfStatement>> ExportAsync(RdfExportContext context, CancellationToken cancellationToken)`

- [ ] **Step 1: Write failing parser and round-trip tests.** Cover URI, blank node, plain literal, language-tagged literal, typed literal, named graph, Turtle, RDF/XML, N-Triples, and rejection of unavailable JSON-LD parsing.
- [ ] **Step 2: Run the tests and verify they fail because parser output uses Oxigraph aliases.**

  ```powershell
  dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~RdfPostgres --no-restore
  ```

- [ ] **Step 3: Implement the term records and rewrite parser conversion.** Keep dotNetRDF parser input, but convert `INode` directly into `RdfTerm`; scope blank-node IDs with `rdfimport_{scope}_{ordinal}`.
- [ ] **Step 4: Implement `RdfTermMapper`.** Map class/property/domain/range/subclass statements to TBox rows, individuals and `rdf:type` to graph entities/facts, and data/object assertions to fact values; preserve graph IRI and unsupported-statement payloads.
- [ ] **Step 5: Implement one-transaction projection.** For `Merge`, insert only new rows; for `Replace`, invalidate or replace the target layer according to the append-only model; on any mapping error roll back all rows and audit events.
- [ ] **Step 6: Rewrite `RdfImportService` to call the projection.** Keep existing request normalization, access checks, parser limits, conflict synchronization, terminology sync, validation, and response DTOs.
- [ ] **Step 7: Run import atomicity and round-trip tests.**

  ```powershell
  dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~RdfPostgres --no-restore
  dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~RdfImport --no-restore
  ```

  Expected: no production code in the touched files references `Oxigraph.*`, `StoreWrapper`, `AddQuads`, `LoadNQuads`, or `Match`.
- [ ] **Step 8: Commit the RDF input/projection slice.**

  ```powershell
  git add src/ISEStudio/Ontology/RdfTerm.cs src/ISEStudio/Ontology/RdfStatement.cs src/ISEStudio/Ontology/RdfImportParser.cs src/ISEStudio/Ontology/RdfTermMapper.cs src/ISEStudio/Ontology/RdfPostgresProjection.cs src/ISEStudio/Ontology/RdfImportService.cs src/ISEStudio.Tests/Ontology/RdfPostgresRoundTripTests.cs src/ISEStudio.Tests/Ontology/RdfImportAtomicityTests.cs
  git commit -m "feat: project rdf imports into postgres"
  ```

---

### Task 5: Rewrite RDF export to read PostgreSQL only

**Files:**
- Create: `src/ISEStudio/Ontology/RdfPostgresExportSource.cs`
- Modify: `src/ISEStudio/Ontology/RdfExportService.cs`
- Modify: `src/ISEStudio/Exports/ExportRunner.cs`
- Modify: `src/ISEStudio/Exports/ExportServiceCollectionExtensions.cs`
- Test: `src/ISEStudio.Tests/Ontology/RdfPostgresExportTests.cs`

**Interfaces:**
- `Task<IReadOnlyList<RdfStatement>> ReadLayerAsync(Guid knowledgeSystemId, RdfLayer layer, CancellationToken cancellationToken)`
- `Task<IReadOnlyList<RdfStatement>> ReadReleaseAsync(Guid releaseId, RdfLayer layer, CancellationToken cancellationToken)`
- `Task<byte[]> ExportAsync(KsContext ks, RdfLayer layer, RdfFormat format, CancellationToken cancellationToken)`

- [ ] **Step 1: Add failing export tests.** Import a TBox and ABox through PostgreSQL, export every supported format, parse the output, and assert graph/language/datatype fidelity; add a test proving later workspace changes do not alter release export.
- [ ] **Step 2: Implement PostgreSQL export source.** Query entity types, relation types, graph entities, facts, axioms, and release statements; reconstruct `RdfStatement` values without creating a graph store.
- [ ] **Step 3: Implement in-memory serializers.** Use dotNetRDF `Graph`/`TripleStore` only as writer input objects, never as persistent stores; use `RdfXmlWriter`, `JsonLdWriter`, and existing text serializers for N-Quads, N-Triples, Turtle, and TriG.
- [ ] **Step 4: Replace `RdfExportService` and `ExportRunner` dependencies.** Preserve current endpoint format and layer DTOs while changing constructors to inject the PostgreSQL source.
- [ ] **Step 5: Run export tests and a source scan.**

  ```powershell
  dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~RdfPostgresExport --no-restore
  Select-String -Path src\ISEStudio\Ontology\RdfExportService.cs,src\ISEStudio\Exports\ExportRunner.cs -Pattern 'StoreWrapper|Oxigraph|OpenReadOnly|new Store'
  ```

  Expected: tests pass and the scan produces no output.
- [ ] **Step 6: Commit the RDF export slice.**

  ```powershell
  git add src/ISEStudio/Ontology/RdfPostgresExportSource.cs src/ISEStudio/Ontology/RdfExportService.cs src/ISEStudio/Exports/ExportRunner.cs src/ISEStudio/Exports/ExportServiceCollectionExtensions.cs src/ISEStudio.Tests/Ontology/RdfPostgresExportTests.cs
  git commit -m "feat: export rdf from postgres"
  ```

---

### Task 6: Replace ReleaseManager and published reads with PostgreSQL snapshots

**Files:**
- Create: `src/ISEStudio/Ontology/PostgresReleaseSnapshotRepository.cs`
- Modify: `src/ISEStudio/Ontology/ReleaseManager.cs`
- Modify: `src/ISEStudio/Ontology/ReleaseService.cs`
- Modify: `src/ISEStudio/Ontology/PublishedDataService.cs`
- Modify: `src/ISEStudio/Ontology/PublishedOntologyService.cs`
- Modify: `src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Entities/ReleaseEntities.cs`
- Test: `src/ISEStudio.IntegrationTests/Releases/PostgresReleaseManagerTests.cs`

**Interfaces:**
- `Task<Guid> CaptureAsync(Guid knowledgeSystemId, string version, Guid? actorId, CancellationToken cancellationToken)`
- `Task PublishAsync(Guid releaseId, Guid? actorId, CancellationToken cancellationToken)`
- `Task<IReadOnlyList<ReleaseStatementEntity>> QueryPublishedAsync(Guid releaseId, ReleaseQuery query, CancellationToken cancellationToken)`
- `Task DeleteAsync(Guid releaseId, Guid? actorId, CancellationToken cancellationToken)`

- [ ] **Step 1: Add failing PostgreSQL release tests.** Verify capture copies the three live layers into `ontology_release_statements`, publication creates/activates `ReleaseDeploymentEntity`, published reads are isolated from later live changes, duplicate publish is rejected or idempotent by contract, and deletion removes only the requested release.
- [ ] **Step 2: Run the focused release tests against Testcontainers PostgreSQL.**

  ```powershell
  dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~PostgresReleaseManager --no-restore
  ```

  Expected: failures identify filesystem artifact assumptions and missing snapshot methods.
- [ ] **Step 3: Implement immutable capture.** Read PostgreSQL live rows in one transaction, canonicalize statements, calculate SHA-256 `StatementHash`, and insert `ReleaseStatementEntity` rows with release and layer foreign keys.
- [ ] **Step 4: Implement publication state transitions.** Set `OntologyReleaseEntity.Status = published`, `PublishedAt`, `PublishedById`, and one active `ReleaseDeploymentEntity`; reject updates to statement rows once published.
- [ ] **Step 5: Replace `ReleaseManager` filesystem behavior.** Remove `_workspace`, `_artifacts`, `_servingRoot`, `_published`, `OpenReadOnly`, `ServingPath`, `WriteKsHeader`, and directory creation; preserve public release DTOs by returning the release ID/version and a PostgreSQL-backed logical location.
- [ ] **Step 6: Update published services and DI.** Inject scoped snapshot/query repositories; do not register `ReleaseArtifactStore` or `ReleaseManager` as singleton mutable storage services.
- [ ] **Step 7: Run release tests and assert no runtime directories.**

  ```powershell
  dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~PostgresReleaseManager --no-restore
  Select-String -Path src\ISEStudio\Ontology\ReleaseManager.cs,src\ISEStudio\Ontology\PublishedDataService.cs,src\ISEStudio\Ontology\PublishedOntologyService.cs -Pattern 'OpenReadOnly|serving|RdfRoot|ReleaseArtifactStore|StoreWrapper'
  ```

  Expected: tests pass and the scan produces no runtime-store references.
- [ ] **Step 8: Commit the release slice.**

  ```powershell
  git add src/ISEStudio/Ontology/PostgresReleaseSnapshotRepository.cs src/ISEStudio/Ontology/ReleaseManager.cs src/ISEStudio/Ontology/ReleaseService.cs src/ISEStudio/Ontology/PublishedDataService.cs src/ISEStudio/Ontology/PublishedOntologyService.cs src/ISEStudio/Ontology/OntologyServiceCollectionExtensions.cs src/ISEStudio/Infrastructure/Persistence/Entities/ReleaseEntities.cs src/ISEStudio.IntegrationTests/Releases/PostgresReleaseManagerTests.cs
  git commit -m "feat: serve releases from postgres snapshots"
  ```

---

### Task 7: Replace ontology, published, and SPARQL query services

**Files:**
- Create: `src/ISEStudio/Ontology/PostgresOntologyQueryService.cs`
- Create: `src/ISEStudio/Ontology/PostgresPublishedQueryService.cs`
- Create: `src/ISEStudio/Sparql/PostgresSparqlQueryExecutor.cs`
- Modify: `src/ISEStudio/Ontology/OntologyService.cs`
- Modify: `src/ISEStudio/Ontology/OntologyViewBuilder.cs`
- Modify: `src/ISEStudio/Ontology/PublishedDataService.cs`
- Modify: `src/ISEStudio/Ontology/PublishedOntologyService.cs`
- Modify: `src/ISEStudio/Sparql/SparqlQueryExecutor.cs`
- Modify: `src/ISEStudio/Sparql/SparqlQueryExecutorServiceCollectionExtensions.cs`
- Modify: `src/ISEStudio/Conflicts/ConflictAgent.cs`
- Modify: `src/ISEStudio/Conflicts/ConflictService.cs`
- Modify: `src/ISEStudio/EntityResolution/ResolutionService.cs`
- Modify: `src/ISEStudio/Knowledge/KnowledgeService.cs`
- Modify: `src/ISEStudio/Knowledge/KnowledgeStatsService.cs`
- Modify: `src/ISEStudio/Ontology/ABoxService.cs`
- Modify: `src/ISEStudio/Ontology/ABoxValidator.cs`
- Modify: `src/ISEStudio/Ontology/ConflictDetection.cs`
- Modify: `src/ISEStudio/Ontology/ConflictDetector.cs`
- Modify: `src/ISEStudio/Ontology/DuplicateJudge.cs`
- Modify: `src/ISEStudio/Ontology/ExternalApiService.cs`
- Modify: `src/ISEStudio/Ontology/ExternalOntologyService.cs`
- Modify: `src/ISEStudio/Ontology/HistoryService.cs`
- Modify: `src/ISEStudio/Ontology/SkosManager.cs`
- Modify: `src/ISEStudio/Ontology/StatementProvenanceService.cs`
- Modify: `src/ISEStudio/Ontology/VocabularyProposalService.cs`
- Modify: `src/ISEStudio/Ontology/VocabularyService.cs`
- Test: `src/ISEStudio.Tests/Infrastructure/Queries/PostgresQueryServiceTests.cs`
- Test: `src/ISEStudio.Tests/Sparql/PostgresSparqlQueryExecutorTests.cs`

**Interfaces:**
- `Task<OntologyResponse> GetOntologyAsync(Guid knowledgeSystemId, CancellationToken cancellationToken)`
- `Task<IReadOnlyList<ABoxAssertion>> GetIndividualsAsync(Guid knowledgeSystemId, string? classIri, CancellationToken cancellationToken)`
- `Task<IReadOnlyList<FactQueryRow>> QueryFactsAsync(Guid knowledgeSystemId, FactQuery query, CancellationToken cancellationToken)`
- `Task<IReadOnlyList<ReleaseStatementEntity>> QueryPublishedAsync(Guid releaseId, ReleaseQuery query, CancellationToken cancellationToken)`
- `Task<QueryResponse> ExecuteAsync(string publicId, string sparql, int maxRows, TokenPrincipal token, CancellationToken cancellationToken)`

- [ ] **Step 1: Add failing query tests.** Cover ontology view rows, classes/properties, individual reads, facts, evidence, neighborhoods, provenance, published isolation, and supported SPARQL `SELECT`/`ASK` patterns.
- [ ] **Step 2: Add rejection tests for unsupported SPARQL.** Assert `CONSTRUCT`, `DESCRIBE`, `INSERT`, `DELETE`, unbounded property paths, cross-KS graph clauses, and missing `LIMIT` beyond the configured cap return `ValidationException`.
- [ ] **Step 3: Implement PostgreSQL query services.** Use EF projections for simple reads and parameterized Npgsql SQL for recursive traversal and aggregate counts; every query takes `knowledgeSystemId` or `releaseId` explicitly.
- [ ] **Step 4: Implement the bounded SPARQL compiler.** Parse only the supported `SELECT`/`ASK` subset needed by current clients, translate triple patterns to joins over `graph_entities`, `relation_types`, `facts`, and release statements, and apply `LIMIT` before materializing results.
- [ ] **Step 5: Replace direct StoreWrapper queries in consumers.** Update conflict, resolution, vocabulary, history, external API, published, and ontology services to use the query/repository interfaces; retain DTOs and endpoint routes.
- [ ] **Step 6: Register all query services as scoped.** Remove singleton `StoreWrapper`, `RdfImportService` constructor paths, and any DI registration that opens a graph store.
- [ ] **Step 7: Run query, API, and Graph tests.**

  ```powershell
  dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~PostgresQueryService --no-restore
  dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~PostgresSparqlQueryExecutor --no-restore
  dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~Graph --no-restore
  ```

  Expected: all focused tests pass and application startup does not create an RDF or serving directory.
- [ ] **Step 8: Commit the query and DI slice.**

  ```powershell
  git add src/ISEStudio/Ontology src/ISEStudio/Sparql src/ISEStudio/Conflicts src/ISEStudio/EntityResolution src/ISEStudio/Knowledge src/ISEStudio/Program.cs src/ISEStudio.Tests/Infrastructure/Queries src/ISEStudio.Tests/Sparql
  git commit -m "feat: replace graph queries with postgres services"
  ```

---

### Task 8: Implement one-time legacy RDF cutover in Migration

**Files:**
- Create: `src/ISEStudio.Migration/LegacyGraphReader.cs`
- Create: `src/ISEStudio.Migration/PostgresCutoverWriter.cs`
- Create: `src/ISEStudio.Migration/CutoverVerifier.cs`
- Modify: `src/ISEStudio.Migration/Program.cs`
- Modify: `src/ISEStudio.Migration/ISEStudio.Migration.csproj`
- Create: `src/ISEStudio.Migration.Tests/LegacyCutoverTests.cs`
- Create: `src/ISEStudio.Migration.Tests/ISEStudio.Migration.Tests.csproj`

**Interfaces:**
- `IAsyncEnumerable<LegacyRdfStatement> ReadAsync(string sourcePath, CancellationToken cancellationToken)`
- `Task<CutoverWriteResult> WriteAsync(IAsyncEnumerable<LegacyRdfStatement> statements, string postgresConnectionString, CancellationToken cancellationToken)`
- `Task<CutoverVerificationResult> VerifyAsync(string postgresConnectionString, CutoverManifest expected, CancellationToken cancellationToken)`
- `record CutoverManifest(IReadOnlyDictionary<Guid, long> Counts, IReadOnlyDictionary<Guid, string> Sha256ByKnowledgeSystem)`

- [ ] **Step 1: Add failing migration tests.** Cover named graphs, blank nodes, unsupported statements, duplicate input, count/checksum equality, transaction rollback on mapping failure, and rerun rejection/idempotency.
- [ ] **Step 2: Add a `cutover` CLI command.** Extend `Program.Main` with `cutover`; require `--source`, `--postgres-connection-string`, `--manifest`, and `--mode`; missing arguments return exit code `1` and print usage.
- [ ] **Step 3: Implement `LegacyGraphReader` as the only legacy input adapter.** It may use the minimal conversion dependency needed to read old data, but returns `LegacyRdfStatement` values and is not referenced by `ISEStudio` production DI.
- [ ] **Step 4: Implement `PostgresCutoverWriter`.** Use one transaction per knowledge system, call the same projection/mapping code as production imports, persist unsupported-statement decisions, and roll back on any failed mapping.
- [ ] **Step 5: Implement `CutoverVerifier`.** Canonicalize statements as UTF-8 N-Quads-like lines, sort them, calculate SHA-256 per knowledge system, compare both counts and digests, and return a failed result plus nonzero CLI exit code on mismatch.
- [ ] **Step 6: Remove direct runtime store usage from the migration project.** The migration command may read legacy input only during `cutover`; it must never create a serving store or be referenced by production DI.
- [ ] **Step 7: Run migration tests and CLI help.**

  ```powershell
  dotnet test src\ISEStudio.Migration.Tests\ISEStudio.Migration.Tests.csproj --no-restore
  dotnet run --project src\ISEStudio.Migration\ISEStudio.Migration.csproj -- cutover --help
  ```

  Expected: migration tests pass; help exits `0`; invalid cutover arguments exit `1`.
- [ ] **Step 8: Commit the migration slice.**

  ```powershell
  git add src/ISEStudio.Migration src/ISEStudio.Migration.Tests
  git commit -m "feat: add postgres legacy graph cutover"
  ```

---

### Task 9: Remove Oxigraph/RocksDB runtime dependencies and verify startup

**Files:**
- Modify: `src/ISEStudio/ISEStudio.csproj`
- Modify: `src/ISEStudio.Migration/ISEStudio.Migration.csproj`
- Modify: `src/ISEStudio.Tests/ISEStudio.Tests.csproj` only if tests still directly require legacy types
- Modify: `src/ISEStudio.sln` to exclude `ISEStudio.OxigraphProbe` from production build/test graphs
- Delete or isolate: `src/ISEStudio/Ontology/StoreWrapper.cs` and `src/ISEStudio/Ontology/QuadChangeCapture.cs` after all references are removed
- Delete or isolate: `src/ISEStudio/Ontology/ReleaseArtifactStore.cs` after all references are removed
- Create: `scripts/verify-postgresql-authoritative-storage.ps1`
- Test: `src/ISEStudio.Tests/Runtime/PostgresOnlyStartupTests.cs`

**Interfaces:**
- Verification script exits `0` only when production and migration project files have no Oxigraph/RocksDB package references, source has no runtime `new Store`, `OpenReadOnly`, `serving/`, or `data/rdf` construction, and DI has no StoreWrapper registration.
- `PostgresOnlyStartupTests` starts the application with a temporary configuration whose RDF root does not exist and asserts PostgreSQL-backed services resolve without creating it.

- [ ] **Step 1: Add the verification script and failing startup test.** Scan only production and migration source/project files; exclude `src/ISEStudio.OxigraphProbe` and explicit test fixtures from runtime findings.
- [ ] **Step 2: Run the script before cleanup.**

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-postgresql-authoritative-storage.ps1
  ```

  Expected: nonzero exit code listing remaining Oxigraph/RocksDB references.
- [ ] **Step 3: Remove production package references.** Delete `Oxigraph`, `Oxigraph.Extensions.DotNetRDF`, and `dotNetRDF` only after all production code compiles without their legacy APIs; retain dotNetRDF only if it is still used strictly by in-memory parser/writer code and document that boundary.
- [ ] **Step 4: Remove migration package references and update project references.** Keep the migration project dependent on PostgreSQL projection contracts, not the web host's runtime storage implementation.
- [ ] **Step 5: Delete legacy runtime classes and registrations.** Remove StoreWrapper, QuadChangeCapture, release artifact/serving construction, nullable Oxigraph service registration, and `RdfRoot` directory creation; retain the probe only as an offline historical tool outside production and migration builds.
- [ ] **Step 6: Run the verification script and package scans.**

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-postgresql-authoritative-storage.ps1
  dotnet list src\ISEStudio\ISEStudio.csproj package --include-transitive
  dotnet list src\ISEStudio.Migration\ISEStudio.Migration.csproj package --include-transitive
  ```

  Expected: script exits `0`; neither package list contains Oxigraph or a RocksDB runtime package.
- [ ] **Step 7: Run build, focused integration, unit, and migration tests.**

  ```powershell
  dotnet restore
  dotnet build src\ISEStudio\ISEStudio.csproj --no-restore
  dotnet build src\ISEStudio.Migration\ISEStudio.Migration.csproj --no-restore
  dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --no-restore
  dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --no-restore
  dotnet test src\ISEStudio.Migration.Tests\ISEStudio.Migration.Tests.csproj --no-restore
  ```

  Expected: all required tests pass. Existing unrelated failures must be recorded with their exact test name and not hidden.
- [ ] **Step 8: Run the no-legacy-directory startup smoke test.** Start the application against PostgreSQL with the configured RDF root absent, call one ontology read, one ABox read, one release read, and one RDF export endpoint, then assert the RDF root and `serving` directories were not created.
- [ ] **Step 9: Commit the dependency and runtime cutover.**

  ```powershell
  git add src/ISEStudio/ISEStudio.csproj src/ISEStudio.Migration/ISEStudio.Migration.csproj src/ISEStudio.sln scripts/verify-postgresql-authoritative-storage.ps1 src/ISEStudio.Tests/Runtime
  git commit -m "chore: remove oxigraph runtime dependencies"
  ```

---

## Acceptance Matrix

| Requirement | Verification |
| --- | --- |
| Ontology edits are PostgreSQL transactions | `OntologyEditorPostgresTests` plus rollback assertion |
| ABox facts are append-only | `ABoxPostgresTests` checks invalidation and supersession |
| RDF import is PostgreSQL-backed | `RdfPostgresRoundTripTests` and `RdfImportAtomicityTests` |
| RDF export has no local store | export source scan plus format round trips |
| Release snapshots are immutable PostgreSQL rows | `PostgresReleaseManagerTests` isolation/publication checks |
| Queries are bounded and KS-scoped | query and SPARQL rejection tests |
| Legacy cutover is fail-closed | `LegacyCutoverTests` count/checksum mismatch tests |
| Production and migration have no runtime Oxigraph/RocksDB | verification script and transitive package scans |
| Startup does not need RDF directories | `PostgresOnlyStartupTests` and smoke test |
| Existing Dovetail user change survives | `git diff -- src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` shows no task changes |

## Self-Review

- **Spec coverage:** OntologyEditor and ABox are Tasks 2-3; RDF import/export are Tasks 4-5; ReleaseManager is Task 6; query services and SPARQL are Task 7; migration cutover is Task 8; dependency and runtime removal are Task 9; schema/repository prerequisites are Task 1.
- **Placeholder scan:** No unspecified implementation steps are used. Every implementation step names the exact method, table, transaction behavior, or command expected.
- **Type consistency:** `RdfTerm`, `RdfStatement`, `FactQuery`, `ReleaseQuery`, and repository method signatures are introduced before their consumers. Existing `CreateGraphEntityCommand`, `RecordFactCommand`, `GraphNeighborhoodQuery`, `GraphEntity`, `GraphFact`, and `QueryResponse` remain compatibility contracts.
- **Known test baseline:** The current repository has an unrelated extraction API test returning `InternalServerError` instead of `Conflict`; this plan does not silently classify that failure as part of the PostgreSQL cutover.
- **User change protection:** No task modifies `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs`.
