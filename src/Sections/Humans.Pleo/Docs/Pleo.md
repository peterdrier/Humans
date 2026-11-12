<!-- freshness:triggers
  src/Sections/Humans.Pleo/**
-->
<!-- freshness:flag-on-change
  The sign rules (totalBillValue first, family only as fallback), the reconciliation strip, and that the sweep never
  deletes, unlinks or changes a hand-linked pleo_categories row.
-->

# Pleo — Section Invariants

A read-only mirror of the Pleo card wallet and the `/Pleo` admin pages over it. Step one of getting Pleo spend into
Holded: Peter reviews the mirror here before anything is booked. Nothing is written to Pleo or to Holded; Finance
books the spend from what `IPleoService` serves (step two, `/Finance/Pleo`).

## Concepts

- **Entry**: one Pleo accounting entry (`/v1/accounting-entries:search`). Families seen live: `WALLET/LOAD`
  (money in), `CARD_PURCHASE/IN_STORE|ONLINE`, `PLEO_INVOICE` (Pleo's fee) and `OUT_OF_POCKET` (money out). Any
  other family/subFamily pair is **unclassified**: listed, counted beside the totals, never guessed.
- **Signed EUR amount**: Pleo's `totalBillValue` when it is EUR (signed, company currency: card refunds come back
  as `CARD_PURCHASE` with a positive value, USD purchases carry Pleo's conversion). Without it, an EUR
  `transactionValue` (always unsigned) takes the family's sign; anything else has no EUR amount and is left out of
  EUR totals and counted.
- **Spend**: completed card purchases, Pleo invoices and out-of-pocket, net of refunds. `PENDING` rows are listed but
  never counted; Pleo's own current balance excludes them too.
- **Category**: Pleo's spending category. An entry's `accountId` is a category id; Pleo's own mapping
  (`/v1/bookkeeping-categories:search`: id, name, chart account) says which account a category books to.
  `pleo_categories` holds the mapping in use: each sweep links the ids nobody linked, and a link made by hand stands
  over Pleo's. An entry whose category is unlinked or absent is **unmapped** and never booked onto a guessed account.
- **Chart**: Pleo's chart of accounts (`/v1-beta/accounts:search`) is Holded's; `code` is the Holded account number.

## Data Model (`PleoDbContext`, history `__EFMigrationsHistory_Pleo`)

| Table | Content |
|---|---|
| `pleo_entries` | one row per entry, keyed by Pleo id; typed fields, signed `AmountEur`, and `Raw` (jsonb, the entry as returned) |
| `pleo_employees` | cardholders: Pleo id, first/last name, code, deleted flag. No email or phone |
| `pleo_accounts` | the chart: Pleo id, code, name, external (Holded) id, archived |
| `pleo_balance` | one row: wallet current, available and pending debits in minors |
| `pleo_sync_states` | one row: status, last run, entries mirrored, empty rows skipped, last error |
| `pleo_categories` | category name → Holded code, linked Pleo category id (unique when set), who set it (null: a sweep) and when |

The first five are a cache, rebuilt by every sweep. `pleo_categories` is not: a sweep only adds to it (see
Invariants). Its 13 seed rows (`HasData`) are Pleo's "Holded Accounts" categories as of 3 Nov 2026, all unlinked. Its name is not unique-indexed
(`unique-constraints-ids-only`); the service refuses a duplicate name. Sentinel table: `pleo_entries`.

## Routing

All under `FinancePolicies.Read`; English-only finance-admin pages like `/Holded`.

- `/Pleo` — configured or not; sync card with Sync now; the **reconciliation strip**: loads in, then spend by family
  (refunds called out), computed balance, Pleo's current balance, the difference in red when not 0.00, and the
  counts left out (non-EUR converted, completed without EUR, pending, unclassified). The two counts that block a
  Holded booking, with totals: unmapped and missing receipt, each linking to its filtered list. Spend by Holded account with
  an Unmapped bucket, spend by cardholder, and "N new categories to map" when an id is waiting.
- `/Pleo/Entries` — every entry, newest first by performed date, filterable by Holded account, cardholder, family,
  no receipt, unmapped (no Holded code); both blocking filters leave loads out.
- `/Pleo/Entries/{id}` — every mirrored field (typed, plus the raw JSON) and its receipts, listed live from Pleo and
  shown inline through `/Pleo/Entries/{id}/Receipts/{receiptId}`. A failed lookup is said on the page. No link into
  Pleo's app: no stable URL pattern is verified.
- `/Pleo/Categories` — unlinked category ids seen on entries (count, spend, top merchants, cardholders, a link
  dropdown of unlinked names) first, then every category (name, Holded code and name — red when missing from or
  archived in the chart — linked id, count, spend), with Unlink, and Add a category.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Finance admin / Admin | Everything (`FinancePolicies.Manage`): Sync now, link, unlink, add a category. |
| Board | Read every page and receipt (`FinancePolicies.Read`). |
| Other sections | Read via `IPleoService` (Finance, for `/Finance` and `/Finance/Pleo`). |
| Any other human | Nothing. |

## Invariants

- A category-link POST with a missing or blank Pleo id returns the existing invalid-id refusal before any mapping save or audit, then redirects to Categories with error feedback.

- The internal `PleoTextBounds` bounds imported entry notes and saved sync errors to 2,000 UTF-16 units and permanent HTTP-error body previews to 300, without splitting surrogate pairs. The preview must remain valid UTF-8 before it becomes part of a stored sync error. The raw vendor entry remains intact, and recording an error preserves the original exception and existing mirror.

- Whoever linked a category id owns its row. A sweep leaves a row a person linked as it is. An id nobody linked is
  linked to the unlinked row of the same name (taking Pleo's account), or added as a new row. A row a sweep linked
  follows Pleo's name and account. A Pleo category with no account, or an account missing from the chart, is skipped.
  A sweep never deletes or unlinks a category row.
- A sweep fetches entries, chart, categories, cardholders and balance before writing anything, then
  replaces the four cache tables and saves the changed category rows in one save: rows the sweep did not return are deleted. A failed call leaves the
  mirror as it was and records the error on the sync row. The category fetch is the exception: if it fails, the sweep
  goes on without it, leaves `pleo_categories` as it is, and keeps the category error on the sync row (shown on the
  sync card) while the status stays `Idle`. If that diagnostic save also fails, the secondary failure
  is logged without replacing the original sync or caller-cancellation exception; the sync gate is still released.
- JSON API responses must have an object root. A null, array or scalar response fails as a typed
  `PleoPermanentException`, so live receipt reads use their existing failure feedback instead of leaking a
  JSON object-access exception. This check also covers all sweep reads before mirror replacement.
- The shared entries/accounts/employees/receipts pager requires an explicit boolean `pagination.hasNextPage`
  on every page; only `false` completes the dataset. Missing, null or mistyped metadata fails instead of
  returning a prefix. A body without `data`, `hasNextPage` without a cursor, a repeated cursor or the 100-page
  cap also throws, so a short fetch can never delete rows.
- Entries without an id or family are skipped and counted on the sync row. A provided `transactionValue` or `totalBillValue` must contain both a nonblank currency and a minor-unit value; incomplete money objects fail the sweep before mirror replacement and record a sync error. Optional money objects may be absent, and explicit zero amounts remain valid. List/detail views omit the original-currency figure when no currency was supplied.
- One sweep at a time: an in-process gate shared by the nightly job and Sync now; a second caller is skipped and
  told so. No key (`PLEO_API_KEY`, `PLEO_COMPANY_ID`): the job logs and skips, Sync now is disabled.
- Employee sweeps request `includeDeleted=true` on every page, retaining former cardholders and their deleted
  flag so historical entries and spend summaries keep their names after someone leaves Pleo. Contact fields
  remain outside the mirror.
- The connector is read-only: GETs and the `:search` POSTs. The key holds write scopes; nothing here uses them.
<!-- wheat: docs/superpowers/specs/2026-08-10-pleo-connector-design.md §Pleo API facts -->
- The mirror polls: Pleo's webhooks cover only export jobs and vendors, nothing per entry. Cardholders come from
  `/v2/employees`; the `/v1/employees` and `/v1/users` paths are denied to a standalone key.
- Receipts are fetched live and never stored. The file comes from Pleo's signed CDN URL (an `https` `*.pleo.io` host
  only), without the API key; anything but an image or PDF is served as `application/octet-stream`. Only that
  response may be framed by its own origin, so the entry page can show a PDF.
- Per-account totals are spend with a linked category; they plus Unmapped equal total spend.
- Display order is the controller's; the service orders only the top-three merchants.

## Audit

Every link, unlink and add on `/Pleo/Categories` writes an audit entry naming the category, the Pleo id or Holded code,
and the actor (`PleoCategoryLinked`, `PleoCategoryUnlinked`, `PleoCategoryAdded`): the mapping decides where spend is
booked. A sweep writes one entry per category row it links, adds or remaps (`PleoCategoryRemapped`), with the job as
actor; otherwise its result is the sync row.

## Negative Access Rules

- Board can **not** sync, link, unlink or add a category.
- A receipt is served only for an entry in the mirror: the route is not a general proxy onto Pleo.

## Triggers

- `pleo-sync`, nightly at 03:15 (Hangfire, `PleoSyncJob`): the full sweep. A 4xx from Pleo ends the run with one warning and no retry;
  transient failures (5xx, 429, network) keep Hangfire's retries.

## Cross-Section Dependencies

- Finance.Contracts: `FinancePolicies` only.
- Users.Contracts: `IUserService`, through `HumansControllerBase`.
- No Holded call and no Holded reference: the Holded codes shown come from Pleo's own chart.

## GDPR

Cardholders are mirrored by Pleo id, name and code only, and bound to no member, so there is no export contributor
or erasure path to hang them on; a sweep drops anyone Pleo no longer returns. Binding cardholders to members (a later
step) brings the export and deletion paths with it. `SetByUserId` on a category is provenance only.

## Architecture

The internal `PleoMirrorReader` owns mirror snapshots, summary/reconciliation and entry/category projections, Madrid-window settled reads, wallet balance, and live receipt reads. `Service` delegates its seven existing read entry points and retains category mutations and forwards sync to `PleoMirrorSync`. Repository read order, live-account preference, spend/refund classification, receipt scope and error handling, logger category and cancellation are unchanged. The reader performs no writes; no public contract or DI registration is added.

### Public contract

`IPleoService` (`Contracts/`): `GetSettledEntriesAsync(from, to)` — entries settled in a Madrid-date window, each with
its Holded code or null and whether it has a receipt. Finance books only entries with both. `GetWalletBalanceAsync` —
the wallet's current EUR balance at the last sweep (`PleoWalletBalance`), null before one or in another currency; the
`/Finance` and `/Finance/Pleo` balance check. Served from the mirror. Everything else is internal.

Internal `PleoMirrorSync` owns the full sweep and its vendor-to-mirror conversions, including the shared non-blocking gate and sync-state reporting. Fetch order, skipped/duplicate-row handling, atomic replacement, timestamps, cancellation, original-error preservation and logger category are unchanged. Category mappings a person linked remain outside the sweep. No public contract, job signature or DI registration is added.
