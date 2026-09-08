*** PostgreSQL Authoritative Storage Migration Design

## Goal

Make PostgreSQL the only authoritative runtime store for ISEStudio graph data.
RDF remains an import/export boundary format. Oxigraph and RocksDB must not be
used as mutable or read-only runtime storage by the production application or
the migration project.

The implementation follows the storage shape already used by
`E:\GitHub\utopia`:

- ontology classes become `entity_types`;
- ontology properties and relations become `relation_types`;
- domain, range, parent and disjointness axioms use link tables;
- ABox individuals become graph entities;
- assertions become append-only facts with evidence and invalidation;
- graph traversal uses PostgreSQL recursive CTEs;
- release snapshots and published projections remain PostgreSQL rows.

## Scope

The migration covers:

- `StoreWrapper`;
- `OntologyEditor`;
- `ABoxManager`;
- `RdfImportService`;
- `RdfExportService`;
- `ReleaseManager`;
- SPARQL and ontology query services that currently read Oxigraph;
- production and migration project dependencies and DI registration;
- schema, EF entities, configurations, integration tests and cutover checks.

The migration does not remove RDF parsing or serialization support. It removes
RDF stores from the runtime persistence path.

## Authoritative Data Model

PostgreSQL is the source of truth for all live graph state:

- `entity_types`: TBox classes, labels, descriptions, IRIs and embeddings;
- `relation_types`: object/data properties, temporal and OWL-style flags;
- `relation_type_domains` and `relation_type_ranges`: multi-valued domain/range;
- `entity_type_parents`: subclass edges and primary display parent;
- `entity_type_disjoint`: symmetric disjointness edges;
- `graph_entities`: ABox individuals and identity/type metadata;
- `facts`: subject/predicate/object or JSON value, validity, confidence and
  invalidation state;
- `fact_evidence`: source chunks, quotes and proposed predicates;
- `ontology_axioms`: any supported axiom that does not fit the dedicated link
  tables;
- `ontology_release_statements`: immutable release statement/provenance rows;
- existing audit, provenance, document and blob tables.

The model must preserve the following semantics:

- facts are append-only; invalidation sets `invalidated_at`;
- corrections use `supersedes_fact_id`;
- entities can have entity-valued or JSON/literal-valued objects;
- one relation can have multiple domains and ranges;
- subclass traversal supports multiple parents and cycle validation;
- release snapshots are immutable after publication;
- all writes affecting one operation use one PostgreSQL transaction.

## Service Boundaries

### PostgreSQL graph repository

Introduce a PostgreSQL-backed repository behind the existing application
services. It owns type, relation, entity, fact, evidence, axiom, neighborhood
and release-statement operations. SQL should use the existing EF Core/Npgsql
model where practical and explicit Npgsql SQL for recursive or bulk queries.

`Oxigraph.Quad` must not appear in the runtime repository API.

### OntologyEditor

Replace StoreWrapper mutations with transactional writes to entity types,
relation types, domain/range links, parent links, disjointness and axioms.
Replace RDF capture/revert with a PostgreSQL transaction. Validation and cycle
checks happen before commit.

### ABoxManager

Replace StoreWrapper mutations with graph entity and fact repository calls.
Use append-only facts, evidence rows, invalidation and supersession. Entity
creation, type changes, assertions and cascade deletion must be represented in
the PostgreSQL audit/provenance model.

### RdfImportService

Keep the existing parser and format detection. Change the post-parse pipeline
to project RDF into PostgreSQL:

```text
RDF/Turtle/RDFXML/N-Triples
    -> parser
    -> TBox/ABox projection
    -> one PostgreSQL transaction
```

The importer maps classes, properties, domains, ranges, subclass edges,
individuals and assertions into the authoritative model. Unsupported
statements are recorded in PostgreSQL import/provenance payloads or rejected
according to an explicit policy; they must never be written to a fallback
Oxigraph store.

### RdfExportService

Read only from PostgreSQL graph and release rows. Reconstruct RDF terms and
named graph boundaries in memory, then serialize to N-Quads, N-Triples,
Turtle, TriG, RDF/XML or JSON-LD. No export path may call a local graph store.

### ReleaseManager

Replace filesystem artifact and RocksDB serving stores with PostgreSQL release
rows and immutable release statement/provenance projections. A publication
records its state in `OntologyReleaseEntity` and `ReleaseDeploymentEntity` and
serves reads using release-scoped PostgreSQL queries. `OpenReadOnly`, serving
directories and per-release RocksDB are removed.

### Query services

Replace Oxigraph SPARQL execution with explicit PostgreSQL query services for
ontology views, entity instances, facts, neighborhoods, provenance and
published releases. If the public SPARQL endpoint remains, it must be a bounded
PostgreSQL query compiler with a documented supported subset; it must not proxy
to Oxigraph.

## Dependency and Runtime Cutover

Remove from production and migration projects:

- `Oxigraph`;
- `Oxigraph.Extensions.DotNetRDF`;
- `dotNetRDF` when it is only used for old-store conversion;
- Oxigraph/RocksDB Store construction and read-only opening;
- `data/rdf` and `serving/{releaseId}` runtime paths.

Keep RDF parser/serializer dependencies only when they do not create or open a
store. Production startup must register PostgreSQL repositories and must not
conditionally register a nullable Oxigraph service for tests.

## Migration and Compatibility

The migration project must provide an explicit one-time cutover path that:

1. reads the legacy store only as an input source;
2. projects every supported TBox/ABox statement into PostgreSQL;
3. records unsupported statements and mapping decisions;
4. verifies statement counts and per-knowledge-system checksums;
5. fails closed on mismatches;
6. leaves production capable of running with PostgreSQL only.

After cutover, the application must not reopen the legacy store. Legacy data
may remain on disk for rollback retention, but it is not an application data
source and must not be required for startup or reads.

## Testing and Acceptance

The implementation is complete only when:

- unit tests cover type/relation/entity/fact repository behavior;
- integration tests run against PostgreSQL and verify recursive neighborhoods,
  evidence, invalidation, release snapshots and RDF round trips;
- imports and edits are atomic on failure;
- published reads are isolated from later workspace changes;
- deleting or corrupting legacy RDF directories does not affect startup or
  PostgreSQL-backed reads;
- `dotnet list ... package --include-transitive` shows no Oxigraph/RocksDB
  runtime dependency in production or migration projects;
- source and configuration scans find no runtime Store construction, RocksDB
  serving path or Oxigraph DI registration;
- existing SQLite contract tests remain explicitly test-only and do not define
  production storage behavior;
- the pre-existing user modification in
  `src/ISEStudio/Extraction/Dovetail/DovetailPipelineRegistrations.cs` remains
  untouched.

## Rollout Order

1. Add schema/entities/configurations and PostgreSQL repository tests.
2. Replace ontology and ABox mutations.
3. Replace RDF import and export projection paths.
4. Replace release capture/publish/read paths.
5. Replace query services and remove Oxigraph runtime registration.
6. Implement and verify one-time legacy cutover.
7. Remove old dependencies, files and compatibility paths.
8. Run PostgreSQL integration, contract, migration and dependency scans.