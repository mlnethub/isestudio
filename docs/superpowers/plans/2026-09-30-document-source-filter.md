# Document Source Filter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an independent Source filter to the knowledge-system document workspace, with server-side filtering that preserves correct empty states, totals, and pagination.

**Architecture:** Pass an optional `source_id` through the existing document-page query envelope. `DocumentService` will filter documents by primary `SourceId` or an existing SourceDocumentBinding before calculating totals; the frontend will keep upload-target selection independent and add a read-only-capable list filter.

**Tech Stack:** ASP.NET Core, EF Core, SQLite/PostgreSQL, React, TypeScript, Vite, Vitest, Playwright.

## Global Constraints

- Follow `docs/superpowers/specs/2026-09-30-document-source-filter-design.md`.
- Omitting `source_id` preserves current document-list behavior.
- A selected Source must belong to the requested knowledge system; invalid and cross-KS Source IDs return a client error.
- Match `Document.SourceId` or an existing `SourceDocumentBindingEntity`; use an existential condition so one document appears at most once.
- Keep the upload-target selector independent from list filtering. Keep virtual-folder navigation and its directory enumeration independent from Source filtering.
- Do not implement client-side filtering of only the current page or add connectors.
- Do not commit changes unless explicitly requested.

## Files and Responsibilities

- Modify `src/ISEStudio.Tests/Documents/DocumentApiTests.cs`: add HTTP-level source-filter contract tests using the existing SQLite test factory.
- Modify `src/ISEStudio/Integration/DocumentApplicationService.cs`: parse optional `source_id` from the existing `InternalRequest.Query` and pass it to the domain service.
- Modify `src/ISEStudio/Documents/DocumentService.cs`: validate Source ownership and compose primary-source/binding predicates with the existing folder, filename, and parse-status filters before total calculation.
- Modify `frontend/src/lib/api.ts`: serialize the optional `sourceId` as `source_id` on `listDocumentsPage`.
- Modify `frontend/src/components/KsDocuments.tsx`: load readable Sources for all roles, add a Source filter, keep the folder-only upload selector write-gated, reset pagination when the filter changes, and pass the filter to the page request.
- Modify `frontend/src/lib/i18n.tsx`: add English and Chinese labels for the Source filter and its all-sources option.
- Modify `frontend/e2e/dotnet/ingestion-sources.spec.ts`: exercise empty Source filtering, restoration of all-source results, and independence from the upload target using the isolated SQLite test backend.

---

### Task 1: Source-aware document-page API

**Files:**

- Modify: `src/ISEStudio.Tests/Documents/DocumentApiTests.cs`
- Modify: `src/ISEStudio/Integration/DocumentApplicationService.cs`
- Modify: `src/ISEStudio/Documents/DocumentService.cs`

**Interfaces:**

- Extend `DocumentService.ListPageAsync` with a final optional `Guid? sourceId = null` parameter so existing callers remain compatible.
- `DocumentApplicationService.ListPageAsync` reads `source_id` from `InternalRequest.Query`; absent means no Source predicate, a malformed GUID is rejected, and a valid GUID is passed through.
- Before querying documents, `DocumentService` verifies that the Source exists in the requested KS. A missing or foreign Source is rejected as a client error.
- The document predicate is `d.SourceId == sourceId || _db.SourceDocumentBindings.Any(b => b.SourceId == sourceId && b.DocumentId == d.Id)`. Apply it before `LongCountAsync` and page slicing. Continue returning the existing KS-wide folder list.

- [ ] **Step 1: Add failing API tests.** In `DocumentApiTests.cs`, next to `ListPage_filters_by_folder_and_status`, add tests that use the existing `AuthTestWebApplicationFactory`, `SeedAdminAndClientAsync`, `CreateKsAsync`, and `UploadBytesAsync` helpers. Create two folder Sources plus an empty Source. Upload two documents, then use the test DbContext to set one document's primary Source to the first Source, the other's to the second Source, and add a `SourceDocumentBindingEntity` from the first Source to the second document. Assert that filtering by the first Source returns its primary document and the shared document once; filtering by the second Source returns its primary document and shared document once; filtering by the empty Source returns `items=[]` and `total=0`. Also assert that an existing `folder=/x` query still returns the KS-wide `folders` array.
- [ ] **Step 2: Add invalid Source cases.** Add a theory that requests the page with a malformed `source_id`, a nonexistent GUID, and a Source belonging to another KS; assert each response is `400 Bad Request` and does not expose another KS's document data. Use distinct KS fixtures for the foreign-Source case.
- [ ] **Step 3: Run the focused tests and verify red.** Run `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~DocumentApiTests`. Confirm the new test fails because `source_id` is ignored or rejected as an unknown contract, not because of test setup.
- [ ] **Step 4: Thread `source_id` through the query envelope.** In `DocumentApplicationService.ListPageAsync`, read the `source_id` query value with `InternalRequestHelpers.QueryString`. If absent, keep `sourceId` null; otherwise parse with `Guid.TryParse` and throw `InvalidOperationException("source_id must be a valid GUID.")` when parsing fails. Pass the optional value as the final argument to `DocumentService.ListPageAsync`.
- [ ] **Step 5: Validate ownership and filter before counting.** In `DocumentService.ListPageAsync`, after the existing role and limit checks, use `_db.Sources.AnyAsync(s => s.Id == sourceId && s.KnowledgeSystemId == ks.Id, ct)` when `sourceId` has a value; throw `InvalidOperationException("source_id must belong to this knowledge system.")` if it is false. Add the Source predicate to the same condition list used for folder, filename, and status before building `baseQuery`. Keep the existing folder enumeration query scoped only by KS, so virtual-folder navigation is unchanged.
- [ ] **Step 6: Run the focused tests and verify green.** Rerun `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~DocumentApiTests`; expect all document API tests, including Source, binding, invalid-ID, folder, and total-count cases, to pass.

### Task 2: Independent UI Source filter

**Files:**

- Modify: `frontend/src/lib/api.ts`
- Modify: `frontend/src/components/KsDocuments.tsx`
- Modify: `frontend/src/lib/i18n.tsx`
- Modify: `frontend/e2e/dotnet/ingestion-sources.spec.ts`

**Interfaces:**

- Add optional `sourceId?: string` to the `listDocumentsPage` params and serialize it to `source_id` only when selected.
- Keep `selectedSourceId` as the upload target. Add separate `selectedFilterSourceId`, where a stable sentinel such as `"all"` means no Source filter.
- Load Source list data for Viewer and Editor/Owner. Show all Source kinds in the list filter; derive folder-kind options for the write-gated upload selector.

- [ ] **Step 1: Extend the frontend API query contract.** In `api.listDocumentsPage`, add `sourceId?: string` to the params type and set `source_id` in `URLSearchParams` when it is present and not the all-sources sentinel. Preserve existing query serialization for folder, text, status, limit, and offset.
- [ ] **Step 2: Add the independent filter state and control.** In `KsDocuments`, add `selectedFilterSourceId` initialized to `"all"`; load the ingestion Source list for every document reader, including Viewers. Render a labeled Source-filter Select beside the existing document controls with an “All sources” option and one option per Source. Keep the existing upload selector on `selectedSourceId`, limited to `kind === "folder"` and visible only when `canWrite`. If the selected filter Source disappears after a Source-list refresh, reset the filter to `"all"`.
- [ ] **Step 3: Apply filter state to requests and pagination.** Pass `sourceId: selectedFilterSourceId === "all" ? undefined : selectedFilterSourceId` to `api.listDocumentsPage`; include `selectedFilterSourceId` in the refresh callback dependencies. Reset `page` to zero when the selected filter changes. Do not alter `cwd`, the folder breadcrumbs, or the upload call's `selectedSourceId` argument.
- [ ] **Step 4: Add localized control labels.** Add English and Chinese translation entries in `i18n.tsx` for the Source filter label and “All sources”; use those keys in the new Select label and option.
- [ ] **Step 5: Extend the isolated browser contract.** In the existing `uploads through a second folder source and preserves it when moving virtual folders` test, create a second empty folder Source in addition to the upload Source. After uploading a document to the upload Source, select the empty Source in the new filter and assert that the document row is hidden and the empty state is shown. Select “All sources” and assert the row returns. Assert the upload-source Select still points to the upload Source, then complete the existing move assertion and verify `source_id` remains unchanged.
- [ ] **Step 6: Run frontend unit and build gates.** From `frontend`, run `pnpm test`, `pnpm build`, and `pnpm lint`; expect all commands to pass, allowing only the repository's already-recorded warnings.
- [ ] **Step 7: Run browser and backend regression gates.** With the isolated SQLite E2E backend configured, run `pnpm exec playwright test --project=dot-net e2e/dotnet/ingestion-sources.spec.ts`; expect the source-filter contract and existing Source UI contracts to pass. Then rerun `dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj --filter FullyQualifiedName~DocumentApiTests` to confirm the final API contract.

## Self-Review

- Spec coverage: independent upload target and list filter are covered by Task 2; `source_id` validation and source/binding semantics are covered by Task 1; folder/search/status composition, totals, empty results, and pagination reset are included in the API and UI test steps; the existing no-filter path remains covered by current document-page tests.
- Scope: no connector, schema, migration, or Source-management changes are needed; the Source binding table already exists.
- Commit handling: no commit is part of this plan unless the user explicitly requests one.
