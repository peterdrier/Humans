# Holded Finance Integration — Expense Actuals Pull + Creditor-Balance Exposure

**Date:** 2026-05-25
**Section:** Finance
**Status:** Design — approved in brainstorm, pending spec review
**Supersedes:** `docs/superpowers/specs/2026-04-26-holded-read-integration-design.md` (purchase-doc pull + `{group-slug}-{category-slug}` dash-split tag matching). That scheme is dead-on-arrival because Holded strips tag separators; this spec replaces it.

This is a **two-remote / v1-only** integration. Everything below was verified against the **live production Holded account on 2026-05-25** (read-only probes; no writes).

---

## 1. API reality (probed 2026-05-25, live account)

| Finding | Detail |
|---|---|
| **v2 does not work** | Every v2 endpoint → `403 Forbidden "Access denied"`, with both `Authorization: Bearer` and the `key` header. v2 requires a registered Holded developer OAuth app with scopes. **Not needed — v1 covers the entire design.** |
| **v1 works** (`key` header) | `contacts`, `chartofaccounts`, `expensesaccounts`, `documents/purchase`, `payments` all 200 with live data. Base `https://api.holded.com`. Key from `HOLDED_API_KEY` env only. |
| **Purchase docs are line-level** | `GET /api/invoicing/v1/documents/purchase` returns each doc with `products[]`; **each line carries `account` (Holded account ID) and `tags`**. Doc also has doc-level `tags`. `accountingDate` is null on real docs → use `date` (epoch s). |
| **Pagination works** | `?page=N&limit=M` respected (distinct pages observed). Unlike the broken `dailyledger`. Loop until an empty page. |
| **Trial balance is uncapped** | `GET /api/accounting/v1/chartofaccounts` → every active account `{id, num, name, balance, debit, credit}` in one call. |
| **Creditor accounts** | `400000xx`, one per contact, **named after the person**, in `chartofaccounts`. **Negative balance = money owed.** E.g. `40000004 "Peter D" balance=-23`, `40000001 "Daniela…" balance=-3180`. |
| **Contact→account link** | `GET /api/invoicing/v1/contacts/{id}` exposes **`supplierRecord.num` = the `400000xx` account number**. (`accountNum`/`account`/`customId` on the contact are null otherwise.) This gives a **robust, non-name-based link**. |
| **Payments** | `GET /api/invoicing/v1/payments` → all rows `{contactId, contactName, amount, date, documentType, documentId}`. One call returns all. |
| **Account provisioning** | `expensesaccounts` lists 34 standard PGC accounts (`{id, accountNum}`). `create-expenses-account` / `createaccount` endpoints exist (v1 family) for creating new accounts. **Write capability with the current key is not yet probed** (see §6). |
| **Tags are separator-stripped** | Confirmed live: tags appear as `adminstaff`, `operationstoilets` (never `admin-staff`). |
| **Starting state** | Today ~all expenses are booked to **`62900000 Otros servicios`** (+ `62600000 Servicios bancarios` for bank/Stripe fees). **Zero category-specific accounts exist.** Only a couple of docs are tagged. |

---

## 2. Feature 1 — Expense actuals → budget categories

Pull Holded expenses and attribute each to a `BudgetCategory`, surfacing **actual vs. allocated** per category plus an **unmatched bucket** for cleanup.

### Granularity
`BudgetCategory` level (Comms, Cantina, Sound, Staff — ~50 categories). This is the level that holds the allocated amount; the parent `BudgetGroup` is the roll-up. (Budget hierarchy: `BudgetYear → BudgetGroup → BudgetCategory → BudgetLineItem`.)

### Account provisioning — in-app page (`/Finance/HoldedAccounts`)
The dedicated accounts are created and maintained from a **Finance admin page**, not a one-off script — so it's idempotent, handles new categories over time, and is operator-driven. Standard reconcile-and-apply pattern:

- **Pull** the current state: Holded accounts (`expensesaccounts`), the `HoldedCategoryMap`, and the live `BudgetCategory` list (via `IBudgetService`).
- **Diff into three buckets:**
  - **Mapped** — category ↔ account already exists (show number + name).
  - **To add** — category with no account yet → preview the number it would receive, the account name, and the dash-free tag.
  - **Orphan** — account in our reserved block whose category no longer exists → retire candidate.
- **Inputs:** the **reserved number block start** (e.g. `62900100`). Numbers auto-increment from the start, skipping any already-used number in the block.
- **Actions:**
  - **Add one (test)** — create the next category's account, verify it appears in `chartofaccounts`. (This is how build-time probe #1 — write access — gets cleared.)
  - **Add all** — create an account for every "to add" category.
- **Additive only.** The page never deletes Holded accounts (Holded likely disallows; unverified — see §6). It is safely re-runnable: new categories appear as "to add" on the next visit.
- **Retire** (you flagged as "if possible", so scoped accordingly): if a Holded account-deactivate/delete endpoint exists (probe), offer it for orphans; otherwise **retire = archive the `HoldedCategoryMap` row in Humans** — the account stays in Holded (unused), historical actuals are preserved, and it's dropped from attribution. The account is never deleted.

Each category also gets a **dash-free tag** (lowercase, strip all non-alphanumerics) recorded in the map as the fallback (B) key, set at provisioning time.

### Attribution (A-primary, B-fallback, bucket)
Per purchase-doc **line**, first match wins:
1. **(A)** line `account` ∈ the category-account map → that category.
2. **(B)** a normalized line/doc tag matches a known category tag → that category.
3. else → **unmatched bucket**.

Rationale: today everything lands in generic accounts, so the bucket starts nearly full — **that is the migration worklist**, and it's what drives the behavior change (book bills to the category account, or tag them). You fix in Holded; the next sync clears the row.

### Data model (Finance-owned tables)
- **`HoldedCategoryMap`** — `BudgetCategoryId · HoldedAccountNumber · Tag · IsActive · ArchivedAt?`. Rows created/maintained by the provisioning page (§ Account provisioning); `IsActive=false` is how an orphan account is retired without deletion.
- **`HoldedExpenseDoc`** — one row per pulled doc: `HoldedDocId` (unique upsert key), `DocNumber`, `ContactName`, `Date`, `Subtotal/Tax/Total`, `Tags[]`, `BookedAccount`, `BudgetCategoryId?`, `MatchStatus`, `MatchSource` (Account/Tag/None), `RawPayload`, `LastSyncedAt`. (Multi-line docs with mixed accounts → attribute at line level; see §6.)
- **`HoldedSyncState`** — singleton job state (mirrors `TicketSyncState`): `LastSyncAt`, `SyncStatus` (Idle/Running/Error), `LastError`, `LastSyncedDocCount`.

### Sync job
Daily (Hangfire) + manual "Sync now". Paginated `documents/purchase` pull (loop until empty page). Doc `date` → budget year (per-year actuals come from doc dates, **not** cumulative account balances). Upsert on `HoldedDocId`. Re-resolve match every run.

### Surfaces
- **Actual vs. allocated** per category on the existing `/Finance` budget view (Σ matched-doc totals, scoped to the year).
- **`/Finance/HoldedUnmatched`** — worklist of unresolved docs with reason + deep link to the doc in Holded. **No in-app editing, no tag write-back to Holded** (you fix in Holded, re-sync clears). This is a deliberate simplification vs. the old spec — it removes the unverified `PUT`-tag dependency entirely.

---

## 3. Feature 2 — Creditor-balance exposure to ER submitters

Show each expense-report submitter what the org owes them and the full round-trip, sourced from Holded.

### Why sourced from Holded (decided)
The "owed to you" number **must** come from the Holded creditor balance, not from Humans's own sum of approved-unpaid ER totals. Proof: a member can front a supplier invoice personally (e.g. Peter paying Daniela). That payable gets reassigned to them in Holded with **no corresponding ER in Humans** — so only Holded knows the true balance. The Humans-internal sum would be blind to it.

### Contact enrichment on push (fixes the missing handle)
When pushing an ER, upsert the Holded contact with:
- **`name` = legal name** (`ExpenseReport.PayeeName`) — the official identity (accountant / SEPA / tax filings).
- **`tradeName` = burner** (`DisplayName`) — recognizability only; **only ever set alongside a legal `name`**. Guard: burner never lands in the official `name` slot; if a legal name is missing, skip `tradeName` rather than substitute the burner.
- `customId = UserId`, `type = creditor`, `iban = PayeeIban`.
- Store the returned **`contactId` on the `ExpenseReport`**.

### Link & data (rides Feature 1's nightly job)
- Link chain: `ExpenseReport.HoldedContactId` → `GET contact` → `supplierRecord.num` → that `400000xx` balance from the nightly `chartofaccounts` pull. **No name-matching.**
- Cache, in the same nightly job: every `400000xx` balance + the `payments` rows. No per-user live calls (quota-trivial even at 500 users).
- **Owed = −(creditor balance).**

### Paid signal (fixes a real bug)
Today paid-detection polls each purchase doc's `paymentsPending`. But treasury pays the **creditor account**, not per-doc — so the current check misses aggregate payments (the playbook's documented warning). Replace with: **creditor balance → 0 / a `payments` row for the contact** marks the submitter's outstanding approved ERs Paid and supplies the payment date.

### Submitter UI
Timeline on the expense view reflecting the round-trip:
`Submitted → Approved → Registered in Holded (€X owed to you) → Paid €X on {date}` → €0.
Aggregates naturally across a user's multiple ERs (the creditor balance already sums them). When the balance **exceeds** the sum of the user's ERs (fronted amounts, manual adjustments), show the remainder as **"other (fronted / adjustments in Holded)"** rather than forcing every euro to tie to an ER.

---

## 4. Org-accounting boundary (explicitly NOT built)

Debt reassignment — e.g. a member fronting a supplier invoice (Peter → Daniela) — is **Part-1 org accounting**, not a Humans feature. It is a **manual Holded journal entry**:

```
Dr  40000001  (supplier/member owed)   €X   → clears their payable
Cr  40000004  (the member who paid)     €X   → adds to their IOU
```

with the member's **proof-of-payment screenshot attached to that entry** (fallback: attach to the original invoice and reference the entry in its memo). Daniela's original invoice remains the expense doc — **no new ER**, which would double-book the already-booked expense.

**Humans only ever reads the resulting balances. It never writes reassignment entries.** If the org later wants member-fronting tracked/approved inside Humans, that is a separate feature, out of scope here.

---

## 5. Architecture notes

- New **Finance** Application/Infrastructure services per design-rules §15h(1) (new section starts at (A), Application-first). `IHoldedRepository` owns the new tables. Cross-section to Budget via `IBudgetService` (read-only: category/year lookups). Audit-log via `IAuditLogService` where applicable.
- Reuse the existing `IHoldedClient` / `HoldedClient`, extending it with: read endpoints (`documents/purchase` paginated, `chartofaccounts`, `payments`, `expensesaccounts`), `create-expenses-account`, and contact upsert. Reuse the existing expense-report outbox/job pattern. Caching per §15.
- New `/Finance/*` routes (FinanceAdmin/Admin-gated, like the rest of the section): `HoldedAccounts` (provisioning), `HoldedUnmatched` (bucket), `HoldedSync/Run` (manual sync). Actuals surface on the existing budget views.
- v1 only; key from `HOLDED_API_KEY` env (never appsettings).
- Add `FinanceArchitectureTests` when the tables land (no-EF-in-service, no cross-section repo injection, no direct `BudgetCategory` table access).

## 6. Build-time probes / open items

1. **`create-expenses-account` write with the current v1 key** — read works; the create has not been exercised. Cleared by the provisioning page's "add one (test)" action before "add all".
2. **Account deactivate/delete endpoint** — needed for the "retire orphan" action. Not confirmed in the docs; likely absent (hence additive-only + retire-by-archive). **Never test delete against production** — verify from the API reference only; if unsupported, retire stays Humans-side (`IsActive=false`).
3. **Pagination terminal behavior** — confirm the loop-until-empty page (default page size returned 15; treat as a page, not a total).
4. **Multi-line docs, mixed accounts/tags** — attribute per line (line `price`), summed per category. Most docs are single-line today.
5. **Reserved number block** — operator input on the provisioning page; must not collide with the existing 34 PGC accounts.

## 7. Docs to update on implementation

- `docs/sections/Finance.md` — replace the "Planned" section (currently the dead dash-split design) with this one.
- Mark `docs/superpowers/specs/2026-04-26-holded-read-integration-design.md` superseded.
