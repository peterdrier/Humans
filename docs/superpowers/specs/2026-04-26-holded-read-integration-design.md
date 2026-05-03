# Holded read-side integration — design

**Date:** 2026-04-26
**Status:** Draft for review
**Issue:** [nobodies-collective/Humans#463](https://github.com/nobodies-collective/Humans/issues/463) — supersedes the original issue body, which was a first-pass sketch; this document is the agreed scope.

## Context

Nobodies Collective uses [Holded](https://www.holded.com) for accounting (Spanish PGC chart of accounts, IVA/IRPF compliance, tax filings). Today there is no link between Holded and the Humans budget feature. The treasurer has to mentally reconcile invoices against budget categories, and "are we over on Sound?" is answered from spreadsheets.

This spec covers **read-only sync** of Holded purchase invoices into Humans, with manual cleanup workflow for unmatched documents and a planned-vs-actual roll-up at category and group level.

## Scope

### In scope (this issue)

- `IHoldedClient` HTTP client over Holded's `/api/invoicing/v1/documents/purchase` endpoint
- `HoldedSyncJob` (Hangfire daily 04:30 UTC) — full-pull, paginated
- New `HoldedTransaction` entity (Finance section) storing matched/unmatched purchase docs verbatim
- New `HoldedSyncState` singleton (mirrors `TicketSyncState`)
- `Slug` fields on existing `BudgetGroup` and `BudgetCategory` (Budget section) — used to compose the Holded tag string
- Tag matching: parse incoming Holded tag as `{group-slug}-{category-slug}`
- `/Finance/HoldedUnmatched` queue UI with one-click reassignment that pushes the corrected tag back to Holded
- `/Finance/HoldedTags` read-only tag inventory page (treasurer's reference for what to tag in Holded)
- Sync state card on `/Finance` dashboard with manual "Sync Now" button
- "Actual" column on `/Finance` year-detail accordion at category and group level, with per-category transaction drill-down
- **New `Finance` section** in `docs/sections/Finance.md`, owning the new entities

### Out of scope

- Outbound invoice creation in Holded (separate issue — barrio store invoicing, target ~May 2026)
- Aggregate ticket-revenue posting to Holded (separate issue — target July 2026)
- Expense-report process (separate brainstorm — process is not yet shaped)
- Chart-of-accounts (PGC) display alongside line items (deferred — endpoint confirmed available, ~34 expense accounts present today, can add when treasurer requests)
- Sync of Holded contacts/suppliers as Humans entities (denormalized `ContactName` is sufficient v1)
- Stripe non-ticket income (separate sync if needed)
- Migration of `TicketingProjection` / `TicketingBudgetSyncJob` from Budget into Finance (acknowledged soft boundary; do not refactor unrelated things)

## Holded API findings (live probe, 2026-04-26)

Verified against the real production Holded account using a read-only API key.

### Endpoints used

| Endpoint | Use | Notes |
|---|---|---|
| `GET /api/invoicing/v1/documents/purchase` | Pull purchase invoices | Returns full doc fields in list response — no per-doc detail fetch needed. Paginated via `?page=N&limit=100`. |
| `PUT /api/invoicing/v1/documents/purchase/{id}` | Push tags back on manual reassignment | **PUT-tag support is unverified** — the production account currently has zero tagged docs. Implementation must verify and adopt the fallback path if PUT does not accept tag updates. |

### Auth

API key via `key` header (32-char token). Configured per environment via env var `HOLDED_API_KEY` — never committed.

### Document shape (real example)

```json
{
  "id": "69d7cb1a87de31856e04138f",
  "docNumber": "F260009",
  "contact": "69d7cb12d4e69ab8a9080b87",
  "contactName": "...",
  "date": 1774994400,
  "dueDate": 1775772000,
  "accountingDate": null,
  "forecastDate": null,
  "approvedAt": 1775685600,
  "draft": null,
  "status": 0,
  "currency": "eur",
  "currencyChange": 1,
  "subtotal": 3000,
  "tax": 180,
  "total": 3180,
  "paymentsTotal": 0,
  "paymentsPending": 3180,
  "paymentsRefunds": 0,
  "tags": [],
  "products": [
    {
      "line_id": "...",
      "name": "...",
      "price": 3000,
      "units": 1,
      "tax": 21,
      "taxes": ["p_iva_21", "p_ret_15"],
      "tags": [],
      "account": "69d52345a6fce1bcf60947b1",
      "retention": 0
    }
  ],
  "customFields": [],
  "from": { "id": "69d7caad598318886d0da4da", "docType": "incomingdocument" }
}
```

### Field semantics — important nuances

- **`accountingDate` is often null** on real docs. Period-assignment for budget-year resolution must fall back to `date`. Both fields are stored.
- **`tax` at doc level is NOT the VAT amount.** For a doc with both IVA and IRPF, doc-level `tax` is `subtotal × IVA% − subtotal × IRPF%`. In the example: `3000 × 21% − 3000 × 15% = 630 − 450 = 180`. Per-line `taxes` is an array of tax-rule codes (e.g. `p_iva_21` = 21% IVA, `p_ret_15` = 15% IRPF retention). For v1 we store raw `Subtotal`/`Tax`/`Total` and do not attempt to extract VAT separately. If VAT-only reporting is needed later, parse from per-line `taxes`.
- **`tags`** is an array at the document level (also exists on each product line, but per design we tag at the document level only — "no screw-level reconciliation"). The wire type of tag elements (string vs object) is unverified because the production account has zero tagged docs. Implementation must inspect when the first tagged doc exists and adapt if necessary; current assumption is plain strings.
- **Date fields are epoch seconds.** Convert to `LocalDate` in Europe/Madrid for budget-period assignment.
- **`approvedAt` is the canonical "approved" signal.** `draft` field shows up as null/empty on approved docs and its non-approved values are unclear. Use `ApprovedAt IS NOT NULL` as the inclusion filter for actuals.
- **`currency`** is lowercase 3-char code. v1 only handles `eur`; non-EUR docs go to the unmatched queue with `MatchStatus = UnsupportedCurrency`.
- **`from`** is set when the doc was created from an uploaded receipt (`docType: "incomingdocument"`). We store `SourceIncomingDocId` so the unmatched-queue UI can deep-link back to the original receipt in Holded.
- **`?paid=` filter** works (verified). **`?starttmp=&endtmp=` filter** works but appears to filter on `accountingDate` and therefore returns 0 results when that field is null on every doc. **No reliable incremental-sync field** — strategy is full-pull each cycle.
- **`/api/invoicing/v1/documents/daily`** is NOT a valid doc type (returns "Undefined type daily"). The original issue body mentioned `daily` ledger-style entries — this is incorrect. Only `purchase` is in scope for v1; other types (`purchaseorder`, `purchaserefund`, `creditnote`, etc.) exist as endpoints but have no data in production.
- **No `/tags` endpoint exists** — confirms the original assumption that Humans owns the tag inventory.
- **`/api/invoicing/v1/contacts/{id}`** returns supplier/client records. v1 does not sync contacts; we denormalize `contactName` onto the transaction.

### Pagination

`?page=N&limit=100`. Loop until empty page returned. Deterministic ordering observed in probes (page 1 and page 2 returned distinct ids).

## Data model

### New entity (Finance-owned): `HoldedTransaction`

**Table:** `holded_transactions`

| Property | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `HoldedDocId` | `string` | Holded `id` (24-char hex). Unique. Natural key for upsert. |
| `HoldedDocNumber` | `string` | `docNumber` (e.g. `F260009`) |
| `ContactName` | `string` | `contactName` denormalized; we do not sync contacts |
| `Date` | `LocalDate` | Convert from `date` (epoch s) using Europe/Madrid |
| `AccountingDate` | `LocalDate?` | From `accountingDate`; often null |
| `DueDate` | `LocalDate?` | From `dueDate` |
| `Subtotal` | `decimal` | EUR, raw |
| `Tax` | `decimal` | EUR, raw (net of IVA − IRPF — not VAT alone) |
| `Total` | `decimal` | EUR, raw |
| `PaymentsTotal` | `decimal` | Paid amount |
| `PaymentsPending` | `decimal` | Outstanding |
| `PaymentsRefunds` | `decimal` | Refunds |
| `Currency` | `string(3)` | Lowercase ISO code; v1 asserts `eur` |
| `ApprovedAt` | `Instant?` | From `approvedAt` (epoch s); null = excluded from totals |
| `Tags` | `jsonb` (`string[]`) | Raw `tags` array from Holded |
| `RawPayload` | `jsonb` | Full Holded JSON, for debugging + future field needs |
| `SourceIncomingDocId` | `string?` | `from.id` when `from.docType = "incomingdocument"` |
| `BudgetCategoryId` | `Guid?` | Matched category (null when unmatched) |
| `MatchStatus` | enum (string) | `Matched / NoTags / UnknownTag / MultiMatchConflict / NoBudgetYearForDate / UnsupportedCurrency` |
| `LastSyncedAt` | `Instant` | Updated every sync that touches this row |
| `CreatedAt` | `Instant` | |
| `UpdatedAt` | `Instant` | |

**Indexes / constraints:**
- Unique on `HoldedDocId`
- Index on `BudgetCategoryId` (drill-down queries)
- Index on `MatchStatus` (unmatched-queue queries)
- Index on `(AccountingDate ?? Date)` for period-aggregation queries (computed via persisted column or query-time `COALESCE`)

**Cross-section FKs:** `BudgetCategoryId` → `BudgetCategory` (Budget) — **FK only**, no navigation property. `OnDelete: Restrict` (refuse to delete a category that has any matched transactions; treasurer must reassign first).

### New entity (Finance-owned): `HoldedSyncState`

**Table:** `holded_sync_states` (singleton row, `Id = 1`).

Mirrors `TicketSyncState` exactly. Fields: `LastSyncAt`, `SyncStatus` (`Idle / Running / Error`), `LastError`, `StatusChangedAt`, `LastSyncedDocCount`.

### Modified entities (Budget-owned): `BudgetGroup`, `BudgetCategory`

Add `Slug` field (`string`, lowercase, no accents/spaces/symbols — Holded-tag-safe).

- Unique on `(BudgetYearId, Slug)` for groups
- Unique on `(BudgetGroupId, Slug)` for categories
- Migration backfills slugs from existing `Name` values using a slugifier (Spanish accent map, dash collapse, idempotent)
- Editable in the existing edit forms with an inline warning on edit: "Existing Holded tags using the old slug will become unmatched on next sync."

### Match-status enum

| Value | Description |
|---|---|
| `Matched` | Resolved to a `BudgetCategoryId` |
| `NoTags` | Doc has empty `tags` array |
| `UnknownTag` | At least one tag, none resolve to a `(year, group, category)` tuple |
| `MultiMatchConflict` | 2+ tags resolve to different categories |
| `NoBudgetYearForDate` | No `BudgetYear` covers `AccountingDate ?? Date` |
| `UnsupportedCurrency` | `currency != "eur"` |

## Sync flow

### Components

| Component | Layer | Responsibility |
|---|---|---|
| `IHoldedClient` / `HoldedClient` | App / Infra | Typed `HttpClient`, key from env, paginated GETs, optional `PUT` tags |
| `IHoldedSyncService` / `HoldedSyncService` | App / Infra | Pull → match → upsert → persist match status; manual `ReassignAsync` |
| `IHoldedTransactionService` / `HoldedTransactionService` | App / Infra | Read queries for views: per-category sums, unmatched list, drill-down |
| `IHoldedRepository` / `HoldedRepository` | App / Infra | Per design-rules §15b — single repo over `holded_transactions` + `holded_sync_states` |
| `HoldedSyncJob` | Infra (Hangfire) | Daily 04:30 UTC, calls `IHoldedSyncService.SyncAsync()` |

### Algorithm

1. Read `HoldedSyncState`. If `Running`, log + skip (prevent overlap). Otherwise mark `Running` and update `StatusChangedAt`.
2. Paginate `GET /api/invoicing/v1/documents/purchase?page=N&limit=100` until empty page. Collect all docs.
3. For each doc:
   - Resolve match via the rules below → produces `MatchStatus` and `BudgetCategoryId?`
   - Upsert `HoldedTransaction` by `HoldedDocId` (insert if new; update all mutable fields if existing; preserve `CreatedAt`)
   - Update `LastSyncedAt`
4. Update `HoldedSyncState`: `Idle`, `LastSyncAt = now`, `LastSyncedDocCount = N`. On exception: `Error`, `LastError = msg`. Log all exceptions (per "always log problems" rule) — never swallow.
5. (Deferred to v1.1) Detect Holded-side deletions: any local `HoldedDocId` not returned this cycle. v1 does not handle this; treasurer can clean up manually.

### Match-resolution rules (in order, first failure wins)

1. `currency == "eur"`. Else → `UnsupportedCurrency`.
2. `BudgetYear` exists whose period contains `AccountingDate ?? Date`. Else → `NoBudgetYearForDate`.
3. `tags` is non-empty. Else → `NoTags`.
4. For each tag: split on the first `-`. The prefix is the group slug; the suffix is the category slug. Look up `BudgetGroup` by `(year, group-slug)`, then `BudgetCategory` by `(group, category-slug)`.
5. Exactly one tag resolves to a valid `BudgetCategoryId`:
   - 0 → `UnknownTag`
   - 2+ resolve to different categories → `MultiMatchConflict`
   - 2+ all resolve to the same category → `Matched` (idempotent)
6. All checks pass → `Matched`, `BudgetCategoryId = matched`.

### Re-evaluation

Every sync re-resolves every doc and overwrites `MatchStatus` and `BudgetCategoryId`. Fixing tags in Holded (or via the unmatched-queue UI) takes effect on the next sync, or via the manual "Sync Now" button.

### Manual reassignment flow

1. Treasurer at `/Finance/HoldedUnmatched` picks a doc, selects a `BudgetCategory` from a dropdown (filtered to current-year categories).
2. POST → `IHoldedSyncService.ReassignAsync(holdedDocId, categoryId)`:
   - Local: set `BudgetCategoryId`, `MatchStatus = Matched`, write `AuditLogEntry` recording who/what/when via `IAuditLogService`.
   - Remote: `PUT /api/invoicing/v1/documents/purchase/{id}` with `tags` array including the computed `{group-slug}-{category-slug}` (de-duped against existing tags).
   - **PUT failure path** (PUT-tag support unverified): keep the local match; surface a warning on the row "Tag not pushed to Holded; please tag manually" with a deep link to the doc in Holded. Log at `Warning` severity.
3. Idempotent: re-running the same reassignment is a no-op.

### Cadence & manual trigger

- Hangfire recurring at `0 30 4 * * *` UTC (04:30 daily) — same convention as `TicketingBudgetSyncJob`.
- Manual "Sync Now" button on the `/Finance` dashboard sync-state card — same pattern as `TicketTailorService` triggers.

### Deletions / drift (v1.1, not in v1)

- Holded-side deletes: scan local rows whose `HoldedDocId` was not returned this cycle and mark deleted. v1 does not implement this.
- Slug edits that orphan tags: v1 just warns at edit time. v1.1 may offer a "bulk re-tag historical docs" action.

## Admin UI

All routes are `FinanceAdmin` or `Admin`. Public `/Budget/Summary` and coordinator `/Budget` views are unchanged in v1.

### Sync state card on existing `/Finance` dashboard

- Last sync time + status (`Idle` / `Running` / `Error` with last-error tooltip)
- Count of unmatched docs (link → `/Finance/HoldedUnmatched`)
- "Sync Now" button (manual trigger)

### `/Finance/HoldedUnmatched` — unmatched queue

Table columns: `Doc # | Date | Vendor | Total | Raw Tags | Reason | → Holded | Action`.

- `Reason` shows `MatchStatus` in plain language ("no tags", "tag `sound-foo` not found", "tags conflict: matches Sound and Site Power", "no budget year covers 2024-08-12", "currency `usd` not supported").
- `→ Holded` deep link: URL pattern is `https://app.holded.com/...` — exact pattern verified during implementation against a known doc.
- `Action`: category dropdown grouped by `Year > Group > Category` (current year only by default, with a "Show all years" toggle) + "Assign" button. Submit → `ReassignAsync`. Optimistic UI: row removes on success.
- Empty state: "No unmatched documents — every Holded purchase is mapped to a budget category."

### `/Finance/HoldedTags` — tag inventory (read-only)

Table grouped by `Year > Group`, one row per category:

| Year | Group | Category | Holded Tag |
|---|---|---|---|
| 2026 | Departments | Sound | `departments-sound` 📋 |
| 2026 | Departments | Cantina | `departments-cantina` 📋 |

Each row shows the computed tag with a copy-to-clipboard control.

### `/Finance` year-detail — Actual column

Add an "Actual" column to the existing tree-style accordion (groups → categories → line items). Sums `HoldedTransaction.Total` where `BudgetCategoryId = this category AND ApprovedAt IS NOT NULL`.

- Category row: `Allocated: €X | Planned (line items): €Y | Actual (Holded): €Z | Variance: …`
- Group row: rolled-up sums.
- Variance color: green if under-budget, red if over (direction-aware for income vs expense categories — negative `AllocatedAmount` is income).
- Click category → expands to a list of `HoldedTransaction` rows (vendor / date / total / `→ Holded` link). This is the "what did Sound spend €23k on?" drill-down.

### Slug fields on existing `BudgetGroup` and `BudgetCategory` edit forms

Text input below `Name`. Hint: "Lowercase, no spaces/accents/symbols. Used as the Holded tag." Auto-populated from `Name` on create (slugified). On edit of an existing slug, inline warning: "Existing Holded tags using the old slug will become unmatched on next sync — fix tags in Holded after saving."

### Nav links (per the no-orphan-pages rule)

- `/Finance/HoldedUnmatched` — reachable from sync state card on `/Finance`.
- `/Finance/HoldedTags` — reachable from a secondary "Tag reference" link in the `/Finance` toolbar.
- Per-category drill-down — reachable inline from the category row on `/Finance` year-detail.

## Authorization

- All Finance section routes require `FinanceAdmin` or `Admin` (matches existing `/Finance/*`).
- v1 does not introduce a resource-based authorization handler — single role gate is sufficient. If per-category coordinator-visible actuals become a feature later, introduce `FinanceAuthorizationHandler` per design-rules §11 at that point.

## Cross-section dependencies

Finance → Budget (read-only):
- `IBudgetService.GetCategoryBySlugAsync(year, groupSlug, categorySlug)` — match resolution
- `IBudgetService.GetYearForDateAsync(date)` — period assignment
- `IBudgetService.GetCategoriesByYearAsync(yearId)` — dropdown for unmatched queue
- `IBudgetService.GetTagInventoryAsync(yearId)` — tag inventory page

Budget never calls into Finance — preserves the planning-vs-reality separation.

Finance → Audit Log (write-only):
- `IAuditLogService.LogAsync(...)` — manual reassignment audit trail.

## Configuration

- `HOLDED_API_KEY` env var (sensitive, not in `appsettings.json`)
- `Holded:SyncIntervalCron` in `appsettings.json` — defaults to `0 30 4 * * *`
- `Holded:Enabled` in `appsettings.json` — defaults to `true`; set `false` to disable the sync without removing the job

## Testing approach

| Layer | Tests |
|---|---|
| `HoldedSyncService` matching | All 6 `MatchStatus` outcomes; multi-tag-same-category; multi-tag-conflict; slug case sensitivity; null `accountingDate` falls back to `date`; non-EUR currency |
| Slug normalization | Spanish accents (`Sonido y Música` → `sonido-y-musica`), trailing/leading whitespace, symbols, double-dash collapse, idempotence (slug of slug = slug) |
| `HoldedClient` HTTP | Mocked `HttpClient` (existing pattern from `TicketTailorServiceTests`): pagination terminates on empty page; query-string shape; key header; error response handling; cancellation |
| Upsert by `HoldedDocId` | Insert new + update existing in one cycle; preserves `CreatedAt`; refreshes `LastSyncedAt`; re-evaluates `MatchStatus` |
| `ReassignAsync` | Local match update + audit-log entry; PUT-tag success path; PUT-tag failure path keeps local match and surfaces warning |
| EF migration | Slug backfill produces unique slugs within parent scope on a real-shaped seed; no nulls; idempotent re-run |
| Architecture | `FinanceArchitectureTests` per design-rules §15h(1) — service in `Humans.Application`, no `Microsoft.EntityFrameworkCore` import, repository is the only `DbContext` toucher |

End-to-end against the live Holded API is out of test scope (production data; cannot pollute). Implementation manually verifies against the live key during dev (the pattern established during this design session).

## Implementation risks

1. **`PUT` tag-update support is unverified.** Production account has zero tagged docs. Implementation must verify and adopt the documented fallback path if PUT does not accept tag updates.
2. **Tag-element wire type (string vs object) is unverified.** Same root cause. First tagged doc will reveal it; adapt deserialization if necessary.
3. **`accountingDate`-null behavior is widespread.** Both real production docs are null. Period-assignment fallback to `date` is in the design — confirm it covers the realistic distribution as more docs land.
4. **`from.docType` enumeration is unverified.** We branch only on `"incomingdocument"`. Other values are ignored; revisit if they prove relevant.

## Open questions

None blocking design. Items that emerged during this session and were resolved:

- Plan vs. actual storage shape — separate entity confirmed (planning lives in `BudgetLineItem`; actuals live in `HoldedTransaction`).
- Tag mapping shape — derived from `BudgetGroup.Slug` + `BudgetCategory.Slug`, not stored in a separate mapping table.
- Section ownership — Finance promoted to its own section; Budget narrows to planning + public summary.
- Unmatched-doc handling — loud surface with one-click reassignment that pushes tag back to Holded.

## Implementation phases (planning hint, not a plan)

The actual implementation plan will be written via `superpowers:writing-plans` after this spec is approved. Hint at phasing:

1. EF migration: add `Slug` to `BudgetGroup` and `BudgetCategory` with backfill; create `holded_transactions` and `holded_sync_states` tables. Run `.claude/agents/ef-migration-reviewer.md`.
2. `IHoldedClient` + `HoldedClient` with paginated GET; manual smoke test against live key.
3. `IHoldedRepository` + `HoldedRepository`; architecture test.
4. `HoldedSyncService.SyncAsync()` with full match-resolution logic; unit tests.
5. `HoldedSyncJob` (Hangfire); manual "Sync Now" endpoint.
6. `HoldedTransactionService` read queries.
7. `/Finance/HoldedUnmatched` UI + `ReassignAsync` (with PUT verification + fallback).
8. `/Finance/HoldedTags` UI.
9. `/Finance` year-detail — Actual column + drill-down.
10. Slug edit-form changes.
11. Section doc finalization, feature doc updates, freshness catalog triggers.
