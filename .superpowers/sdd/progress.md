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
