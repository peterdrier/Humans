<!-- freshness:triggers
  src/Sections/Humans.Finance/**
  src/Sections/Humans.Finance.Contracts/**
  src/Sections/Humans.Holded/Services/HoldedClient.cs
  src/Sections/Humans.Holded/Services/HoldedContactsClient.cs
  src/Sections/Humans.Holded/Services/Service.cs
  src/Sections/Humans.Budget/Controllers/BudgetAdminController.cs
  src/Sections/Humans.Expenses/Services/ExpenseReportService.cs
-->
<!-- freshness:flag-on-change
  FinanceController routes or auth policy (FinanceAdminOrAdmin) — review when FinanceController or the section's cross-section contracts (IBudgetService, IHoldedService, IUserService) change. Holded attribution logic (Account → Tag → Managed → Unmatched) and the provisioning model reviewed when HoldedMatcher, IHoldedFinanceService, HoldedCategoryMap, or the managed-account registry (HoldedManagedAccount) change.
-->

# Finance — Section Invariants

Finance is the **treasurer's reality side** of the money story. Budget owns planning and public presentation; Finance owns actuals, reconciliation, and treasurer-facing operational data. The two share `BudgetGroup` / `BudgetCategory` keys; nothing else.

The end-to-end *business* process — how a reimbursement actually moves from expense report through the bank and back to a zero balance, including the manual steps and external parties — is [`expense-reimbursement-process.md`](expense-reimbursement-process.md). This file stays about code invariants.

What lives here: the Holded purchase-doc sync and its attribution to budget categories; creditor bindings and the balances derived from the Holded section's ledger mirror ([Feature 2](#feature-2--creditor-reads-over-the-holded-sections-mirror)); and SEPA payout generation and booking ([Feature 3](#feature-3--sepa-payout-of-creditor-balances), [`features/sepa-payout.md`](features/sepa-payout.md)). The Budget surface on the same `/Finance` prefix is Budget's — see Routing.

## Concepts

- A **SEPA payout** is one generated pain.001.001.09 file over a set of member creditor *balances*, kept verbatim with one row per credit transfer. It settles nothing by itself: the treasurer uploads it to the bank, then books each transfer on `/Finance/Sepa`, and the next ledger sync clears the balance.
- **Booking** a transfer settles it against the outgoing Sabadell line that paid it (nobodies-collective/Humans#1185): the bound member's open purchase documents are paid oldest first from the configured treasury account, dated the bank line, and whatever they do not cover is one journal entry, debit the creditor account, credit the bank's ledger account. Sabadell debits one line per transfer but drops the remittance text, so a line is matched to a transfer on amount and date; a line whose text does name the account is matched on that too. A recurring sweep every two hours books only text-matched lines unattended; amount-matched lines wait for a finance admin's **Book file** on `/Finance/Sepa`, offered only when every unbooked transfer in the file has exactly one line, and booking them all in one go. It stamps `BookedAt`, the acting admin (null = the sweep), `HoldedBankMovementId` and — once Holded confirms the pairing — `ReconciledAt` onto the transfer row. It is the only thing that moves a transfer out of `Generated`, and it cannot be done twice.
- A **Holded Expense Doc** is a purchase invoice pulled from Holded and stored verbatim. Each line is attributed to a budget category via the attribution chain below.
- **Attribution chain (Account → Tag → Unmatched):**
  1. **Account (A):** the line's booked Holded `account` id is looked up in `HoldedCategoryMap.HoldedAccountId`. Match → `MatchSource = Account`.
  1b. **Managed (A′):** the booked account id is in `holded_managed_accounts` → `Matched`, `MatchSource = Account`, `BudgetCategoryId = null`.
  2. **Tag (B):** each raw tag is normalized (lowercase, non-alphanumeric stripped — Holded strips separators like dashes) and compared against `HoldedCategoryMap.Tag`. First hit → `MatchSource = Tag`.
  3. **None:** doc lands in the **unmatched bucket** (`MatchStatus = Unmatched`, `MatchSource = None`).
- A **Holded Category Map** row joins a `BudgetCategory` to its dedicated Holded account number/id and its dash-free fallback tag. `IsActive` is the retirement flag, but **nothing retires a row today** — a category deleted in Budget shows as an `Orphan` on the provisioning page and its map row stays active, so `IsActive` is always `true`. Holded accounts are never deleted.
- The **Provisioning page** (`/Finance/HoldedAccounts`) reconciles the live Holded chart-of-accounts against the local `HoldedCategoryMap`: diffs into Mapped / ToAdd / Orphan. "Add one (test)" / "Add all" create accounts in Holded + map rows locally. Additive only. Each account created writes a `HoldedCategoryAccountProvisioned` audit entry under the acting admin.
- The **Holded Sync State** is a singleton row tracking the operational state of the recurring sync job (`Idle / Running / Error`).
- A **Managed Account** is an expense account Finance created or linked outside the budget map on another caller's request (`CreateOrLinkExpenseAccountAsync`). Finance records number, id, label and an active flag — never who asked. Docs booked to one are Matched with no category: off the Unmatched queue, outside every year's actuals. Create, link, retire and restore each write an audit entry (`HoldedExpenseAccountCreated` / `Linked` / `ActiveChanged`).
- The **Unmatched Queue** (`/Finance/HoldedUnmatched`) is the working surface where the treasurer inspects unattributed docs and triggers a re-sync.
- The **Connector index** (`/Finance/Holded`) is the read-only answer to "what is Finance's half of Holded doing": doc-sync status with its age rendered as a **Stale** badge rather than left implied, the live `HoldedCategoryMap` rows, every pulled `HoldedExpenseDoc` with its match source and raw tags, and the counts that link out to the four working screens. It never calls Holded — cache reads only, so it cannot inherit the live-contacts timeout `/Finance/Creditors` carries (nobodies-collective/Humans#976). Its read model is `IHoldedFinanceAdminService`, section-**internal**, the same shape as the Holded section's `IHoldedAdminService`.

- **Cash flow** (`/Finance/CashFlow`) answers "when does the money move, and do we run out?" from the active budget year's plan: every line item plus a synthetic VAT settlement per dated line item with VAT (income owes its VAT portion as "VAT Liability", an expense reclaims it as "VAT Credits"; Spanish quarterly VAT settles 45 days after the quarter end), bucketed by ISO week or calendar month with income, expenses, net and running net. Undated items are totalled under **Unscheduled**. The **Funds exhausted** marker is an expense-only runway: it starts at the gross revenue of the year's paid ticket orders (the year's `VendorEventId`, else the current event, selected by Tickets' `TicketOrderInfo.IsForEvent`), subtracts each period's expenses and ignores income, and marks only the first period it reaches ≤ 0. `CashFlowService` (internal) computes it from `IBudgetService.GetActiveYearAsync` and `ITicketService.GetTicketOrdersAsync`; it owns no tables. Planned next step: fold in actuals — Holded purchase documents, expense reports and invoices — beside the plan.

- **Payment details** are the bank account a member is reimbursed to: one `finance_payment_details` row per member, read by Expenses through `IFinanceService.GetIbanAsync` and written through `IFinanceWrite.SetIbanAsync` (Users decomposition commit 1; the IBAN used to live on `profiles.Iban`). Admins see it masked on the Payment Details card of `/Users/Admin/{id}` and can reveal it once per page load, audited.

## Data Model

### HoldedExpenseDoc

**Table:** `holded_expense_docs`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| HoldedDocId | string | Unique. Natural key for upsert. |
| DocNumber | string | e.g. `F260009` |
| ContactName | string | Vendor name, denormalized. |
| ContactId | string(64)? | Holded `contact_id` from the list; null when the doc names no contact or the row predates the column and has not been re-synced. The expense reconciliation matches contacts on it. |
| Description | string? | Holded's internal description (`description`; "Add an internal description" in its UI). Trimmed; blank stored as null. Shown beside the doc on `/Finance`, `/Finance/Holded` and the unmatched queue. |
| Date | LocalDate | From Holded `date` (epoch s, Europe/Madrid) |
| Subtotal | decimal | EUR, raw |
| Tax | decimal | EUR, raw (net of IVA − IRPF) |
| Total | decimal | EUR, raw |
| Currency | string(3) | Lowercase ISO; v1 only handles `eur` |
| Notes | string? | Holded `notes`. The v2 list has none, so the sync GETs each push-written document (description `ER: `) whose notes the mirror lacks, once, at most 50 a run and stopping at the first transient failure (other documents are never fetched and stay null); the upsert keeps stored notes when a sync did not fetch them. Null = not fetched yet (or the GET failed; the next sync retries); empty = Holded holds none. |
| IsApproved | bool? | `false` = still a Holded draft → excluded from actuals. Null = row predates the column and has not been re-synced; treated as not approved. |
| TagsJson | string (jsonb) | Raw tag list from Holded |
| BookedAccountId | string? | First product line's Holded account id |
| BudgetCategoryId | Guid? | Attributed category (null = unmatched) |
| MatchStatus | HoldedMatchStatus | `Matched` or `Unmatched` |
| MatchSource | HoldedMatchSource | `None`, `Account`, or `Tag` |
| LastSyncedAt | Instant | Updated every sync that touches this row |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Cross-section FKs:** `BudgetCategoryId` → `BudgetCategory` (Budget) — FK only, no navigation property. `OnDelete: Restrict`.

### HoldedCategoryMap

**Table:** `holded_category_map`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| BudgetCategoryId | Guid | FK-only, no nav (cross-section) |
| HoldedAccountNumber | int | Reserved account number in Holded |
| HoldedAccountId | string | Holded's internal account id |
| Tag | string | Dash-free normalized fallback tag (Holded strips separators) |
| IsActive | bool | Always `true` today — nothing flips it; see Concepts |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

### HoldedManagedAccount

**Table:** `holded_managed_accounts`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| HoldedAccountNumber | int | Unique. Reserved account number in Holded. |
| HoldedAccountId | string | Unique. Holded's internal account id. |
| Label | string | The name the account was created or linked with. |
| IsActive | bool | Retirement flag. |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

### HoldedCreditorContact

**Table:** `holded_creditor_contacts`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | The bound member. Bare FK, no nav (cross-section). **Unique** — one binding per member. |
| HoldedContactId | string(64) | Holded's contact id; the stable key for every push and ledger lookup. |
| SupplierAccountNum | int? | The resolved 400000xx. Null until Holded assigns one — see the one-shot resolution in Invariants. Indexed, **deliberately not unique**. |
| Source | CreditorContactSource | `Auto` (created by the first expense push) or `Manual` (bound by an admin). String-converted, max 16. |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Cross-section FKs:** `UserId` → `User` (Users) — FK only, no navigation property.

Carries more invariants than any other table here: at most one member per account *and* per Holded
contact, three write paths with different collision remedies, and no unique index to enforce either
— see Invariants for all of it.

### HoldedDocSyncState

**Table:** `holded_doc_sync_state` (singleton, `Id = 1`, persisted on the first explicit sync-state save)

Fields: `LastSyncAt`, `Status` (`Idle / Running / Error` string), `LastError`, `StatusChangedAt`, `LastSyncedDocCount`. Status of the purchase-doc sync only — the ledger mirror and its own sync states are the **Holded section**'s (`src/Sections/Humans.Holded/Docs/Holded.md`); Finance reads it via `IHoldedService`.

### SepaPayoutFile

**Table:** `sepa_payout_files`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK. Also the source of the file's `MsgId`. |
| GeneratedAt | Instant | Indexed. |
| GeneratedByUserId | Guid | The finance admin who pressed Generate. Bare FK, no nav (cross-section). |
| FileName | string(128) | The download name, so a copy still on the treasurer's disk can be identified. |
| Checksum | string(64) | SHA-256 of the UTF-8 XML, lowercase hex. |
| Xml | text | The generated file, verbatim. |

**Cross-section FKs:** `GeneratedByUserId` → `User` (Users) — FK only, no navigation property.

### SepaPayoutTransfer

**Table:** `sepa_payout_transfers`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK. Tags every Holded posting the booking makes; never changes. |
| FileId | Guid | The file it belongs to. Indexed; intra-section, no nav property. |
| UserId | Guid | The member paid. Bare FK, no nav (cross-section). Indexed. |
| SupplierAccountNum | int | The creditor account the balance was read from. |
| HoldedContactId | string(64)? | The Holded contact whose recipient details this transfer actually paid, captured at generation — Holded lets two contacts share one account, so this is what the booking guard checks alongside `SupplierAccountNum`. Null on a row generated before nobodies-collective/Humans#1146; those keep the account-only guard. |
| CreditorName | string(70) | As written into `Cdtr/Nm` — already SEPA-normalized. |
| Iban | string(34) | **Unmasked.** Stored as written into the XML. |
| IbanMasked | string(34) | What logs, audit entries and cross-section rows use instead. |
| Amount | numeric(12,2) | EUR. |
| BookedAt | Instant? | When the payment was booked into Holded. Null **is** the "not booked" state — there is no status column. |
| BookedByUserId | Guid? | The finance admin who booked it, or null when the sweep booked it unattended. Bare FK, no nav (cross-section). |
| HoldedBankMovementId | string(64)? | The Holded treasury bank-movement id (the Sabadell line) this transfer was booked against (nobodies-collective/Humans#1185). Null on a row booked before this shipped. |
| ReconciledAt | Instant? | When the bank line was reconciled against the postings in Holded. Null while the reconcile is pending or was never attempted (a row booked before nobodies-collective/Humans#1185). |
| HoldedPaymentRefs | text? | Comma-joined Holded payment ids the transfer posted (read off the document's `payments_detail` after `POST purchases/{id}/payments`, which returns no id), in posting order, `unconfirmed:` sentinels included, plus `entry:<amount>` when a journal entry was posted (`SepaPayoutTransferRow.JournalEntryAmount` reads it for the row's text). Written with the booking, and by a run that posted payments and then stopped short of booking (`PostedButNotBooked`: the retry refuses). The sweep re-issues the bank-line reconcile from the confirmed ids while `ReconciledAt` is null. Unbounded since the 512 cap overflowed at about 20 documents. |

| WithdrawnAt | Instant? | When a finance admin withdrew the transfer because the bank cancelled it by hand before execution (peterdrier/Humans, 2026-10-18 double payment). Kept for the record and the Article 15 slice; no matcher, booking or payability gate sees the row again. Only an unbooked row can be withdrawn, so this and `BookedAt` are never both set. |
| WithdrawnByUserId | Guid? | The finance admin who withdrew it. Bare FK, no nav (cross-section). |

**Cross-section FKs:** `UserId`, `BookedByUserId` and `WithdrawnByUserId` → `User` (Users) — FK only, no navigation property.

### PaymentDetails

**Table:** `finance_payment_details`

| Property | Type | Purpose |
|----------|------|---------|
| UserId | Guid | PK. The member — bare id, no FK to `users`. |
| Iban | string? (34) | Normalized, **unmasked**. Null records a removal, so the read does not fall back to the legacy profile value. |
| UpdatedAt | Instant | Last write. |

Replaces `profiles.Iban` (Users), which is no longer written. Until the `finance.iban-to-payment-details` data migration (on `/Debug/DataMigrations`) has copied a member's legacy value, a member without a row reads `ProfileInfo.Iban` — the `[Obsolete]` field kept for exactly this reader (`HUM_PROFILE_IBAN`). Once production reports the migration done, a retirement PR deletes the migration, the fallback and the obsolete field, and a drop PR removes `profiles.Iban`.

Account merges preserve the survivor’s effective payment details first, otherwise the source’s, including values still only on legacy profiles. An explicit cleared row on either account suppresses that account’s legacy value; retries preserve the same result.

Payment-detail reads, sets and clears resolve merged member IDs through `IUserService` before selecting the row. A write through an archived source ID updates the survivor's row rather than recreating source details. IBAN reveals audit that same resolved owner; the requested source ID never substitutes for the owner of the data revealed.

### HoldedMatchStatus

| Value | Description |
|-------|-------------|
| Matched | Attributed to a `BudgetCategoryId` |
| Unmatched | No account or tag hit; sits in the unmatched bucket |

Stored as string via `HasConversion<string>()`.

### HoldedMatchSource

| Value | Description |
|-------|-------------|
| None | Unmatched (no attribution found) |
| Account | Attributed via the line's booked Holded account |
| Tag | Attributed via a normalized tag fallback |

`HoldedDocSyncState.Status` is a plain string (`Idle` / `Running` / `Error`), not an enum.

## Routing

Every `/Finance/*` route except the one below is gated on `PolicyNames.FinanceAdminOrAdmin`, declared separately on each of the two controllers serving the prefix — this section's `FinanceController` and Budget's `BudgetAdminController`.

### Member payment details

| Route | Purpose |
|-------|---------|
| `POST /Finance/Admin/Members/{id}/RevealIban` | `AdminOnly` (`PaymentDetailsAdminController`). Audits `IbanReveal`, hands the unmasked IBAN to the next render of the Payment Details card through TempData, redirects to `/Users/Admin/{id}` |

The Payment Details card itself is `PaymentDetailsViewComponent`, contributed to `UserPartSlots.AdminDetailSidebar` by `IUserPart`; it is reached from the admin human-detail page, so it needs no nav entry of its own.

### Not this section — the Budget surface on the same prefix

`Humans.Budget`'s `BudgetAdminController` keeps `[Route("Finance")]`, so `/Finance`, `/Finance/Years/*`, `/Finance/Categories/*`, `/Finance/LineItems/*`, `/Finance/AuditLog` and `/Finance/Admin` are served by Budget. Their table lives in [`Budget.md`](../../Humans.Budget/Docs/Budget.md).

### Cash flow

| Route | Purpose |
|-------|---------|
| `GET /Finance/CashFlow?period=weekly\|monthly` | The active year's cash flow (see Concepts). Any other `period` renders monthly. No active year → flash and redirect to `/Finance`. Linked from the `/Finance` year page toolbar |

### Holded integration

| Route | Purpose |
|-------|---------|
| `GET /Finance/Holded` | Connector index — doc-sync health with explicit staleness, the live category map, the managed-account registry, every pulled doc, and the way into the four screens below. Read-only, cache only (nobodies-collective/Humans#1000) |
| `GET /Finance/HoldedAccounts` | Account provisioning UI (reconcile + apply) |
| `GET /Finance/HoldedUnmatched` | Unmatched-doc worklist with deep links and "Sync now" |
| `GET /Finance/Creditors` | Admin overview of all cached 400000xx creditor accounts with member bindings and, per bound contact, the count of open (approved, still-owing) purchase documents — one live `ListPurchaseDocumentsAsync` call per render, "—" when unbound, colliding or unreadable |
| `GET /Finance/Creditors/{accountNum:int}` | Per-account creditor statement (Holded's balance sign-flipped so positive means the organisation owes the member and negative means the member owes it; plus itemized journal lines, newest first, each with the running owed balance accumulated from the account's first line; a `payment` line shows as "Payment: NCA → {member}", or "Payment: {member} → NCA" when the member pays in, instead of the purchase text Holded matched it to; below the ledger, the expense reports booked to the account, from `IExpenseReportService.GetReviewQueueAsync` filtered on `HoldedSupplierAccountNum`, so both sides of each booking can be compared). "See in Holded" opens the same account's ledger at `app.holded.com/accounting/ledger/{accountNum}` |
| `GET /Finance/Sepa` | Generated payout files and their transfers, with each transfer's booking state and the reason it cannot be booked. Makes one live `ListBankMovementsAsync` call to match transfers to Sabadell lines (nobodies-collective/Humans#1185); a failure banners and every row falls back to "waiting for the Sabadell line" |
| `POST /Finance/HoldedAccounts/Provision` | Add one or all pending Holded accounts + map rows |
| `POST /Finance/HoldedSync/Run` | Manual sync trigger |
| `POST /Finance/Creditors/Bind` | Manually bind a member to a Holded creditor account by 400000xx number |
| `POST /Finance/Creditors/Unbind` | Clear a member's creditor binding (the remedy for a wrong bind or a collision) |
| `POST /Finance/Sepa/Generate` | Build and stream a pain.001.001.09 credit-transfer file over the selected creditor balances |
| `POST /Finance/Sepa/Book` | Book one transfer against a specific bank line: pay the member's open Holded purchase documents, post the remainder, reconcile the line, and stamp the booking onto the transfer row |
| `POST /Finance/Sepa/BookFile` | Process: book every unbooked transfer of one file against the single bank line the bank debited the whole file as, then reconcile that line once |
| `POST /Finance/Sepa/BookMatches` | Book file: book every unbooked transfer of one file, each against the line the page paired it with on amount and date; refused unless every one of them is matched. Carries the page's `days` tolerance, as Book and Process do |
| `POST /Finance/Sepa/Withdraw` | Withdraw one transfer the bank cancelled by hand before execution: stamps `WithdrawnAt`/`WithdrawnByUserId`, audits, emails the member. Refused once the row is booked, tied to a bank line or has posted payments |
| `GET /Finance/HoldedDuplicates` | Dry run of the duplicate purchase documents the expense push created twice: per member the kept id, the duplicate ids, the refusals, the orphan contacts; grand totals. Live Holded reads, nothing written |
| `GET /Finance/ExpenseRecon?year=` | Expense-report vs Holded reconciliation (peterdrier/Humans#2207): totals with a "0 problems" health line, problem lines first, orphan documents, per-member ledger vs expected balance with open SEPA transfers. Local mirrors only, no Holded call, no write, no fix buttons (it links to Duplicate docs, SEPA transfers and Creditors). Linked from `/Finance/Creditors` and `/Finance/Holded` |
| `POST /Finance/HoldedDuplicates/Apply` | Delete the duplicates (documents, then the emptied contacts), one audit entry per id, then a ledger sync; renders what happened with the creditor balances before and after |

The provisioning preview, unmatched queue, creditor overview (including member-name lookup), and creditor statement forward the browser abort token through their existing read APIs. Remote provisioning and payout writes remain independent of the browser connection.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| FinanceAdmin, Admin | Every `/Finance/*` route: provision Holded accounts, run the sync, work the unmatched queue, bind and unbind creditors, generate and book SEPA payouts, view cash flow. |
| Admin | Additionally: the Payment Details card on `/Users/Admin/{id}` and the audited IBAN reveal. |
| Department coordinator | None — Finance routes are FinanceAdmin-only. |
| Any other authenticated human | None |

## Invariants

- Finance marks account provisioning, create-or-link expense accounts, and creditor-contact creation as external writes. State-changing HTTP callers must not pass browser cancellation into those Holded mutations.

- Holded provisioning POSTs require an explicit account-block start and one/all choice. Missing fields and binding errors return HTTP 400 and a Warning after actor resolution, before planning or creating accounts. Missing actors retain Challenge; explicit false add-all flags remain valid.

- The SEPA payout list forwards request cancellation through payout loading and member-name lookup.
- Provisioning-plan, expense-account, category-map and connector-overview reads retain caller cancellation through the active Budget year lookup.

- A purchase doc is attributed **as a whole, by its first product line's** booked account (plus the union of doc-level and line-level tags), and its full `Total` lands on that one category. A multi-line doc booked across several Holded accounts is not split; line-level attribution is a deliberate later refinement (`HoldedDocumentSync.MapDoc`).
- Actuals are keyed on the **calendar year** of the doc's Europe/Madrid date, matched against `BudgetYear.Year` parsed as an integer (`BudgetAdminController` → `GetActualsForYearAsync` → `IHoldedRepository.GetMatchedForYearAsync`). A budget year whose `Year` string is not a plain number, or that does not run January–December, shows no actuals.
- Only `FinanceAdmin` or `Admin` may access any `/Finance/*` route (`[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` on `FinanceController`).
- `FinanceController` performs no budget mutations at all — its actions are the Holded, creditor, SEPA-payout and cash-flow surface. Budget CRUD on the same prefix is `Humans.Budget`'s `BudgetAdminController`.
- `/Finance/Holded` issues **no** Holded HTTP call. Every figure comes from `holded_doc_sync_state`, `holded_category_map`, `holded_expense_docs` and `holded_creditor_contacts`; the mirror's own health (API budget, ledger sweeps, chart of accounts) is `/Holded`'s and is linked, not restated. Pinned by `GetConnectorOverview_MakesNoHoldedApiCall`.
- The doc sync counts as **stale** at 36 h — the nightly job's 24 h cadence plus half a day of grace — and never having run counts as stale too: budget actuals and the unmatched queue are equally wrong either way, so both raise the same alarm.
- Each successful full pull replaces the purchase-document cache, including an empty snapshot. Documents absent from Holded are removed from actuals and unmatched review. Retained rows match on `HoldedDocId` and preserve their identity and `CreatedAt`. A failed fetch leaves the cached documents unchanged.
- Full-pull is forced by a Holded API limitation (live probe, 2026-04-26): the purchase-documents endpoint's only date filters filter on `accountingDate`, which is null on most real purchase docs, so there is no reliable incremental-sync key for purchase documents. `ListPurchaseDocumentsAsync` therefore takes no date window at all — it walks `/api/v2/purchases` page by page internally under a 200-page safety cap. Approval state comes from that same pull: each item carries a `draft` boolean, mapped onto `HoldedPurchaseDocListItemDto.IsDraft` — a separate `?approval_status=draft` sweep was dropped (live probe, 2026-08-25: the param validates but returned a stale/incomplete set; `?draft=true` on the list endpoint is silently ignored). `HoldedDocumentSync.MapDoc` treats an absent `draft` field as not-yet-approved, same caution the old sweep applied to an id it couldn't place.
- Attribution runs every sync. Fixing an account mapping or tag in Holded takes effect on next sync or via the manual "Sync Now" button.
- Attribution order: **Account** (booked line account id) → **Tag** (normalized, dash-free) → **Unmatched**. First match wins.
- Tags are normalized: lowercase, all non-alphanumeric characters stripped (Holded strips separators like dashes from tag values).
- Provisioning is additive only, and nothing retires a map entry today: `IsActive` is set `true` on insert and never flipped, so an orphaned row (its category not in the active year) stays active in the map. `ListExpenseAccountsAsync` reports such a row as an inactive option and drops it with `activeOnly`, so no new report or program books to it while old reports keep their label. Holded accounts are never deleted.
- Created accounts are named exactly as the caller asked and numbered from `62900100` past every number in the category map, the managed registry and the live chart. A name that normalizes equal to an existing chart account (trim, whitespace, case, accents) links instead of creating.
- `GetActualsForYearAsync` returns the per-category total **and the approved docs it sums** (`HoldedActualRow.Docs`, newest first). Budget's year page renders them under the category so a wrong Holded figure can be traced to the document behind it; a draft excluded from the total is absent from the list too.
- Every link to a Holded purchase doc is `https://app.holded.com/expenses/list#open:purchase-{HoldedDocId}` (`Service.HoldedDocUrl`) — Holded has no stable per-doc page, so its list opens the doc from the fragment.
- `HoldedExpenseDoc.Total` is included in category-level actuals only when `IsApproved = true` — set on sync as `doc.IsDraft == false` (`HoldedDocumentSync.MapDoc`). Actuals are doc-derived rather than ledger-derived because the budget pages are gross/IVA-inclusive while a 629 balance is net, and ledger lines exist for drafts Holded has not approved.
- Holded API key read from env var `HOLDED_API_KEY_V2` only — never `appsettings.json`.
- The member ↔ creditor-account link resolves through the Holded contact's `supplierRecord.num` field, never by name matching. It is attempted **exactly once**, best-effort, during outbox processing after the payable exists (`ExpenseReportService` → `IHoldedClient.GetContactAsync`); a failure or a null `num` is logged, the null link is stored, and the outbox event is still marked processed so a created doc is never stranded as permanently-failed. **There is no automatic retry** — the Holded section's `IHoldedService.SyncLedgerAsync` imports daybook lines but never re-resolves the contact — so after an initial miss the member stays unlinked until someone runs `POST /Finance/Creditors/Bind`, or a later report from the same member resolves it and backfills the member-level binding (nobodies-collective/Humans#972). `ListCreditorAccountsAsync` returns exactly these unresolved bindings as the `Unresolved` half of its result — they have no account row to sit on, so the account list alone cannot show them — and they render in their own card on `/Finance/Creditors`, making the manual step discoverable rather than silent.
- A 400000xx account — and the Holded contact behind it — binds to **at most one member**. All three write paths test for a conflicting binding (`FindConflictingBinding`, on both the account number and the contact id: after the one-shot number resolution misses, a binding carries a contact id with a null `SupplierAccountNum`, which an account-number-only check cannot see). They differ in the remedy, because only one of them is a guess:
  - **`SetCreditorContactAsync`** (manual bind) — an admin picked the account, so the pick can be wrong: **refuse and write nothing** (nobodies-collective/Humans#974).
  - **`SetCreditorAccountNumAsync`** (automatic, after the push resolves `supplierRecord.num` from a live `GetContactAsync`) — Holded assigned that number to the contact just pushed, so Holded is authoritative and the *older* binding is the wrong guess. Refusing would strand a created payable against a wrong row, so it **writes the truth, logs Error, and leaves the collision standing** on `/Finance/Creditors` for a human (nobodies-collective/Humans#975). Whenever the number it writes differs from the binding's — a first resolution from unresolved, or Holded reassigning the contact a new account as on 2026-09-21 (40000004 → 40000060) — it writes a `HoldedCreditorAccountNumChanged` audit entry under the calling job's name (Expenses passes `HoldedExpenseOutboxJob`) naming the member, the contact and both numbers; re-recording the same number writes none.
  - The **`seedContactId` / `seedAccountNum` lazy-seed** through **`EnsureCreditorContactAsync`** — this is *our own cache* off the member's prior report, not something Holded just told us, so it is a guess and gets the manual bind's treatment: a seed landing on another member's binding is **refused and dropped**, and the push mints this member their own Holded contact instead. This is also what makes Unbind durable — see below.
- The DB index on `SupplierAccountNum` is deliberately **non-unique**. A unique index would turn a data anomaly into a `DbUpdateException` inside unattended outbox drain — stranding a created Holded doc as permanently-failed — and would have to be created against production rows that may already collide. Enforcement lives in the service (`memory/architecture/db-enforcement-minimal.md`).
- `ListCreditorAccountsAsync` returns **every** binding on an account (`HoldedCreditorAccountRow.Bindings`), not the first, and decides a binding's row **through the Holded contact id**, falling back to the stored `SupplierAccountNum` only for a contact Holded's list does not carry. Which 400000xx a contact holds is Holded's fact, and resolving through it does two jobs. A binding whose number never resolved reaches its row at all — keyed on the number alone the account renders "unbound" while a member in fact holds the contact behind it (the invisibility the #974 second guard's error message ran into), and such a binding could not be unbound from the page. And because the two columns are independent, bindings sharing a contact can carry numbers that disagree; the contact resolution lands them on one row, so the **contact-id half** of the invariant surfaces as a collision instead of two innocent-looking single-member rows. It depends on the live Holded contact list, so it degrades with the names when Holded is unreachable. `/Finance/Creditors` renders each bound member with an **Unbind** button (`POST /Finance/Creditors/Unbind` → `ClearCreditorContactAsync`) and sorts collisions to the top. Unbind removes the whole binding row rather than nulling `SupplierAccountNum`: a binding stripped of its number still carries the other member's Holded contact id, which merges their payables just as thoroughly. The member's next push re-resolves the contact from scratch. Bind and Unbind each write an audit entry under the acting admin (`HoldedCreditorBound` / `HoldedCreditorUnbound`), related to the member; GDPR erasure drops the binding through the same delete without one.
- **Unbind is durable against restoring another member's binding, not against re-deriving the member's own.** Deleting the row is not by itself enough: `ExpenseHoldedPublisher.PublishAsync` seeds the next push from the cleared member's prior report, which still carries whatever contact id and 400000xx were cached on it. The seed-refusal above is what closes that loop — after unbinding a wrong binding, the seed points at the other member's contact, is refused, and the member gets their own new Holded contact. A member's *own* contact still re-derives from their linked history on the next push; that is the documented lazy-seed self-heal and it restores the correct value, not a wrong one.
- **Unbind holds against a push already in flight, in the steady state only.** `ExpenseHoldedPublisher.PublishAsync` spans several Holded calls, so the drain can be mid-push for tens of seconds while an admin clicks Unbind. `EnsureCreditorContactAsync` therefore writes nothing when the member already holds a contact and the binding already carries its 400000xx: the contact id and `Source`/number come off the binding just read, so the only column that would change is `UpdatedAt`, which nothing reads — and writing it would resurrect a binding the admin cleared, from the copy read before they clicked. A binding still missing its number, and `SetCreditorAccountNumAsync`, write real content, so neither can skip; both re-read the binding just before writing and write nothing when it changed since the push read it (`BindingUnchangedAsync`, nobodies-collective/Humans#995). That narrows the race to the gap between the re-read and the write — accepted at this scale rather than closed with a version column ([`no-concurrency-tokens`](../../../../memory/architecture/no-concurrency-tokens.md)). The one write that race must not leave half-done is the contact itself: two drains that both find the member unbound each POST a Holded contact, and the loser would otherwise carry on with a contact no binding holds. The binding's unique `UserId` index decides the winner — a run that minted a contact and then finds the member already bound (on the re-read, or as the insert-only `TryInsertCreditorContactAsync` returning false on the unique violation — a minted contact's binding is never written through the upsert, whose update branch could overwrite a winner's binding) **deletes its own contact from Holded** (`IHoldedClient.DeleteContactAsync`, 404 = success; the delete writes a `HoldedDuplicateRemoved` audit entry under the calling job's name, a failed delete is logged at Error and the orphan surfaces as an empty unbound creditor contact on `/Finance/HoldedDuplicates`) and uses the bound contact, so one member never ends up with two Holded contacts. Holded v2 has no writable `custom_id` and no safe lookup key (name and IBAN would risk binding the wrong member), so there is no look-up-before-POST.
- Creditor accounts are the `40000000`–`41999999` block (`CreditorAccountMin`/`Max`) — it spans both the 400-series proveedor accounts older members carry and the 410-series acreedor accounts a new ER-only contact mints (contacts stay type "creditor"). Every read that draws on Holded's contact list must filter to it — Holded assigns a supplier number to *every* supplier contact, so an unfiltered list turns ordinary org vendors into bindable member creditor accounts. `SetCreditorContactAsync` validates the posted number against the block server-side — the filtered dropdown is not a gate.
- **A linked contact is trusted and never updated in Holded.** Once a binding (or the lazy-seed from a prior report) carries a contact id, `EnsureCreditorContactAsync` uses it as is and makes no Holded call; only a member with no contact gets a POST. `IHoldedClient.CreateContactAsync` is create-only — it has no update path to reach for. Holded's v2 contact PUT is a full replacement — every omitted field resets, `supplier_record` included — and the partial update the push used to send minted a second creditor account next to the member's first (2026-09-21). A legal-name or IBAN change after the first push therefore does not reach Holded until the link-check sync exists (peterdrier/Humans#1777); [`holded-contact-put-is-full-replacement`](../../../../memory/code/holded-contact-put-is-full-replacement.md).
- **Unbound is a valid state, not an error.** A first-time submitter has no creditor contact until their first push; `EnsureCreditorContactAsync` creates it and `SetCreditorAccountNumAsync` records the assigned 400000xx. The bind control exists only for a *pre-existing* Holded contact the auto-create would duplicate. Unbound does **not** imply no contact: `holded_creditor_contacts` was created empty (no backfill), so a member linked before it existed carries a contact id on their older reports only. `ExpenseHoldedPublisher.PublishAsync` therefore seeds `EnsureCreditorContactAsync` from the member's most recent linked report when the report being pushed has no contact id of its own — a null seed makes the client POST a second contact and splits their payables. That push writes the missing binding, so the gap self-heals on first interaction rather than by data migration.
- `ListCreditorAccountsAsync` reads the Holded contact list (`ListContactsAsync`) through a 2-minute `IMemoryCache` entry (design-rules §15 Option A, `CacheKeys.HoldedContacts`) rather than calling Holded live on every load — the 400000xx account **name** lives only in Holded, and the short TTL keeps a contact created today visible without a nightly-cache lag or a per-request call. `ListContactsAsync` itself paginates internally (walks `page` until an empty page returns), so the cached list is never silently truncated. It degrades to blank names when the Holded call fails — transport failure, a rejected key, or an unreadable body — rather than failing the page; unexpected exception types propagate.
- **Never index a nested Holded JSON node directly.** Holded serializes an absent sub-record as an empty *array* (`"supplierRecord": []`), and `JsonNode`'s string indexer throws `InvalidOperationException` on anything that is not a `JsonObject`. Combined with the degrade-to-blank rule above, one such contact blanked every account name on the bind card and `/Finance/Creditors` in production (nobodies-collective/Humans#994). `HoldedContactsClient.ParseContact` reads through `Prop(node, name)`, which yields null for a non-object, and the list parse isolates each contact so one unreadable row costs only its own name. `ListCreditorAccountsAsync` logs when *no* account resolved a name — the all-or-nothing signature of this failure — since it was otherwise silent until a human noticed.

- **SEPA payout is balance-based, and stamps nothing outside this section.** `POST /Finance/Sepa/Generate` builds a pain.001.001.09 credit-transfer file over selected *creditor balances*, never over expense reports — no report status, no member field and no `Paid` flag moves. Settlement closes the ordinary way: the payment is booked in Holded and the next ledger sync zeroes the balance. A partial payout is legitimate; the remainder stays visible as owed.
- A creditor row is **payable** only when it is bound to exactly one member, its balance is positive from the member's side, and its Holded contact carries an IBAN. `/Finance/Creditors` shows the reason instead of a checkbox for every other row, and `GenerateSepaPayoutAsync` re-derives the same three rules server-side — the page is display, not the gate.
- `/Finance/Creditors` defaults to an **owed-only** client-side filter (checked by default) that hides every row with no positive balance; unchecking it reveals settled and negative-balance accounts too. Display-only — it does not change what `GenerateSepaPayoutAsync` will accept.
- SEPA generation rejects malformed account-list or amount-dictionary binding with 400 after
  actor resolution. It never generates a file from the valid subset of a malformed selection.
- **Generation is all-or-nothing.** One bad row — over the cap, over the balance, more than two decimals, below €0.01, an IBAN that fails its check digits, the same creditor account twice — refuses the whole file with a message, and nothing is persisted. A partially-sent batch is far harder to reconcile than a re-run.
- The per-transfer cap is entered on `/Finance/Creditors` and posted with the batch — the posted value is authoritative, not `Sepa:MaxPayoutPerTransfer` (default **€50**, the field's prefill only). `FinanceController.GenerateSepa` parses it invariantly, same reasoning as the amount boxes: an `<input type="number">` posts invariant text, and model binding would read it through the request culture. Unparseable or non-positive refuses the whole batch.
- The organisation's own SEPA identity — `Sepa:CreditorName`, `Sepa:CreditorIban`, `Sepa:CreditorIdentifier` (the NIF + suffix presenter id) — is **configuration-bound and never inferred**. With any of them unset, `/Finance/Creditors` says payout is unavailable and names the missing keys instead of offering a button. `Sepa:CreditorBic` is optional per the Sabadell guide.
- Every generated file is validated **in-process against the official ISO 20022 XSD** (embedded at `Resources/pain.001.001.09.xsd`) before it can reach a browser; `SepaPaymentFileBuilder.Build` returns only files that validate. The builder is pure — no IO, no clock, no configuration — so all of its rules are unit-tested directly.
- IBAN validation requires two ASCII check digits and ASCII account letters/digits before the checksum. Malformed Unicode input is refused before XML generation; valid account letters and existing spacing normalization remain supported.
- The file's `GrpHdr/CreDtTm`, requested execution date, and download filename all use Europe/Madrid local time. `CreDtTm` carries no zone suffix, so writing a UTC clock value there would mislabel it as local time.
- `MsgId` is `"M"` + the persisted file id (33 chars, inside the 35 cap). `PmtInfId`, `InstrId` and `EndToEndId` all carry the same `"<account>-NCA-<NAME>"` (uppercase, SEPA subset, name truncated to fit 35) so whichever one Sabadell echoes on the debtor statement names the creditor account. Holded postings are tagged with the transfer row id instead, which never changes.
- **One `PmtInf` per transfer, each with exactly one `CdtTrfTxInf`.** Sabadell ignores `BtchBookg` `false` inside a block and booked a three-transfer batch as one bank line, which breaks the per-payment matching; banks that ignore the flag still book one debit per `PmtInf`. `GrpHdr` keeps the file-level `NbOfTxs` and `CtrlSum`.
- The file omits postal addresses, `CdtrAgt`, `ChrgBr` and every category-purpose code entirely — **never `SALA`**, which would route a reimbursement as payroll. `RmtInf/Ustrd` is a single occurrence, capped at 140, and carries **this transfer's own** creditor account number and the payee's name (`"<account> - NCA - <creditor name>"`, `NCA` the fixed org tag) so a bank line ties back to an account and a person without opening the file. `EndToEndId` is unaffected.
- Names and remittance text are folded into the restricted SEPA subset (`SepaText`: accents decompose, Ø/Æ/ß and friends map by hand, anything else becomes a space) and capped at 70/140. XML-reserved characters are escaped by the writer, not stripped.
- **A payout pays the contact the binding names, never "the account's first contact".** Holded lets two contacts carry one 400000xx, so both the row on `/Finance/Creditors` and the transfer resolve their name and IBAN through `ContactsByIdAsync` keyed on `CreditorContactBinding.HoldedContactId`. Keying by account number instead would let a singly-bound row display one member and pay another. Creditor statements use the same bound contact identity for their full contact header. An unbound account shows a header only when exactly one contact holds it; multiple bindings or ambiguous unbound contacts leave the header blank while retaining the ledger. Binding-to-account resolution considers every contact by ID before falling back to a stored account number, including sibling contacts sharing a number.
- **The unmasked IBAN is stored only as sent**: the generated XML and `sepa_payout_transfers.Iban`. Every log line, audit entry and cross-section row goes through `IbanFormatter.Mask` ([`iban-mask-in-logs`](../../../../memory/code/iban-mask-in-logs.md)) — including `HoldedCreditorAccountRow.IbanMasked`, which is masked precisely because it crosses a section boundary. A screen shows it in full only to an admin (`/Finance/Creditors/{accountNum}`) or to the member whose IBAN it is.
- Each generated file is persisted **verbatim** (`sepa_payout_files`: the XML, a SHA-256 checksum, the timestamp and the generating admin) with one `sepa_payout_transfers` row per credit transfer, and one `AuditAction.SepaPayoutTransfer` entry per transfer. Rebuilding the file from columns would not survive a builder change; the bytes the bank got are the record.
- **Booked is `BookedAt != null`, and nothing else.** There is no status column and no state machine: `/Finance/Sepa` renders `Generated` or `Booked` off that one field, and `BookSepaTransferAsync` refuses a transfer that already has it. Idempotency is the row, not the UI — a re-POSTed form pays nothing.
- **Withdrawn is `WithdrawnAt != null`, and a withdrawn row is invisible to everything but the record.** `WithdrawSepaTransferAsync` stamps it only on a row with no `BookedAt`, no `HoldedBankMovementId` and no `HoldedPaymentRefs` — once any of those is set the money moved or may have, and the refusal says which. Every matcher entry point (`GetSepaPayoutsAsync`, the sweep, Book, Book file, Process) drops withdrawn rows first (`Live`), so a withdrawn row is never paired, never counts toward a file's "every transfer paired" gate, never blocks a later transfer of the same amount, and never makes its creditor account unpayable. It still renders on `/Finance/Sepa` (badge, who, when) and in the Article 15 slice. Nothing is deleted. One `AuditAction.SepaPayoutTransferWithdrawn` entry and one `sepa_payout_withdrawn` email to the member.
- **A creditor account is not payable while a transfer to it is in an unbooked payout file**, nor while its member has duplicate documents in Holded. `/Finance/Creditors` shows the reason in place of the checkbox (`in payout file {name}, not booked yet` / `duplicate documents in Holded`, linking to the page) and `GenerateSepaPayoutAsync` re-derives both and refuses. The first gate releases on booking, withdrawal, or the row ageing past the 90-day feed window; the second when the duplicates are removed. When Holded's document list cannot be read the flag is unknown (`HasDuplicateDocuments == null`) and the payout refuses rather than guesses — the 2026-10-18 double payment is why both gates fail closed.
- **Duplicate documents are matched by one pure rule, never a stored flag** (`HoldedDuplicateMatcher`): for each expense line that links to a Holded document, every other document on any of the member's contacts with the same `description` (`ER: <line description>`) and the line's booked total is a candidate duplicate; a document some line links to is never anyone's duplicate, so two identical lines with two documents are not flagged, and an extra document matching several identical lines is claimed by the first of them only (counted and deleted once). `notes` (`Expense report <id>`) is **not** part of the rule: the v2 purchase list carries no `notes` field (verified live 2026-10-21 — requiring it is why every consumer reported zero from #2034 until this was fixed). A line belongs to its submitter as they are now: `GetHoldedBookingsAsync` resolves a since-merged submitter to the survivor, the member the creditor binding sits on. The same rule drives the nightly log line in the document sync (Warning with counts, or an Information "zero" line), the `/Finance/Creditors` banner and payability, the payout refusal, and `/Finance/HoldedDuplicates`. The nightly line and the creditors/payout gate stop at the list-level rule, without the GET below: a false positive there only refuses a payout, the safe direction.
- **Apply deletes only what the rule names, the single GET confirms, and Holded will let go.** Before the page lists a candidate as deletable, and again when Apply runs (the plan is rebuilt from live reads, never trusted from an earlier dry run), each candidate not already refused is fetched with `IHoldedClient.GetPurchaseDocumentAsync` — the one read that returns `notes` — and the claim is then run again with the notes known (`Find` with a notes-aware predicate), so a candidate goes to the line whose notes it carries, not to the first line with the same description and total: two reports with identical commute lines each get their own duplicate. A candidate whose notes match no line is listed as a refusal (`notes do not match the push`) under the line that named it, never deleted. A failed fetch fails the whole plan: nothing judged, nothing deleted. A duplicate carrying a payment (`payments_pending < total`, which is also how a reconciled bank line shows) or still a draft is listed for a human, never deleted, and is not fetched. A contact the race created beside the bound one is deleted only once every document on it is gone; the bound contact never is. Documents go before contacts, one `AuditAction.HoldedDuplicateRemoved` entry per id naming member, amount and the kept id, then one ledger sync, so the balances the page shows afterwards are real. The whole run takes no `CancellationToken`.
- **The expense reconciliation is computed, never stored** (`ExpenseRecon`, pure over local snapshots; `IHoldedFinanceService.GetExpenseReconAsync`). The expected side is `IExpenseHoldedBookingService.GetHoldedReconLinesAsync`: every bookable line of every approved report after the cap allocation (a zeroed line has no document and is absent), linked lines of other statuses, and legacy report-level documents (matched by id only), each with its report's outbox state. The actual side is the `holded_expense_docs` mirror, matched by `HoldedDuplicateMatcher` over the same member contact set the duplicate check uses — the mirror's contact names stand in for the live contact list, so a twin is a same-named contact on the mirrored documents — with mirrored notes, where known, as the tiebreak. Line status, first match wins: no document in the mirror → **Pending** when the push is queued/retrying or settled after the mirror's last sync, else **Missing**; another document matching → **Duplicated** (count, excess €); total ≠ booked → **AmountMismatch**; contact ≠ the member's bound one → **WrongContact**; else **Booked**. Pending is not a problem. **Orphans** are mirrored documents carrying the push's marker (notes `Expense report …` or description `ER: …`) that no line keeps or counts as a duplicate, split by whether their contact is bound. **Members** (always all-time, whatever the year filter, because ledger balances are): ledger mirror balance of the bound account against expected = Σ booked SEPA transfers − Σ expected amount of the member's lines whose document is in the mirror; |difference| < €0.005 is **Balanced**, else **Off**; no account is **NoAccount**, a problem only once Holded should hold something of theirs. Open transfers (generated, not booked, not withdrawn) are listed beside the member: a member Off by exactly an open transfer was paid at the bank and is waiting on SEPA booking. The year filter narrows lines (by the document date the push gives, Madrid) and orphans (by mirror date). Problems = problem lines + orphans + members off; the document mirror never having synced, or an empty ledger mirror, makes the report unavailable instead.
- **`/Finance/Creditors` banners include the reconciliation**: red when it has problems (linking to `/Finance/ExpenseRecon`), yellow when it cannot be computed. Payability is unchanged: only the duplicate check and unbooked files gate a payout.
- **Booking allocates oldest first and never over-pays a document.** The transfer amount is spread across the bound member's *approved, still-owing* Holded purchase documents in document-date order, `min(remaining, payments_pending)` each, stopping when the amount is allocated. A draft document is never paid — it books nothing to the ledger, so a payment against it would post to a document that does not exist for accounting.
- **Booking settles the balance, not just the documents.** When the open documents total less than the transfer — or there are none, as with a loan the member made — the remainder is one journal entry, debit the transfer's creditor account, credit `Sepa:TreasuryLedgerAccount`, same date and description as the payments. Nothing is refused for lacking a document behind it: the file paid the balance, and the balance is whatever built it.
- **`Sepa:TreasuryAccountId` and `Sepa:TreasuryLedgerAccount` are required to book and never inferred.** Omitting `treasury_id` lets Holded pay from whichever account it defaults to, which is not necessarily the account the SEPA file drew on; the ledger account is the same bank's `572xxxxx`, the credit side of the remainder entry. Either unset, `/Finance/Sepa` says so once for the whole screen and offers no buttons.
- **A booking that fails mid-allocation is retryable, never terminal** (nobodies-collective/Humans#1185). Whether Holded refused a document payment or the journal entry, the ids already accepted are logged and audited (`PARTIAL`), but **nothing is written to the transfer row** — it stays unbooked. The next attempt re-reads what is already posted under the transfer's own tag and continues from there, so nothing is ever posted twice.
- **A booking runs to completion regardless of the admin.** `BookSepaTransferAsync` takes no `CancellationToken` at all, so a closed tab cannot tear a half-applied set of Holded payments ([`cancellation-token-propagation`](../../../../memory/architecture/cancellation-token-propagation.md)).
- **The binding must still name the account the file was built against.** Booking resolves the member's *current* `HoldedCreditorContact`; when its `SupplierAccountNum` no longer matches the transfer's, the booking is refused — paying a different creditor account than the bank statement's `Ustrd` quotes is unreconcilable. The screen shows the same reason. No rebind history is stored; the transfer's own account number is the record.
- **And it must still name the same contact.** The account number alone is not enough: Holded lets two contacts share one 400000xx, so a rebind to a sibling contact slips past the account check. Booking compares the transfer's own `HoldedContactId` — captured at generation — against the binding's, and refuses when they differ; the file paid the contact named on the transfer, and anything else pays the wrong recipient's documents. `/Finance/Sepa` shows the same reason in place of the Book button, so a doomed booking is never offered. A transfer generated before nobodies-collective/Humans#1146 shipped carries no contact id and keeps the account-only behaviour — the guard never retires a row it has no evidence about.
- One `AuditAction.SepaPayoutTransferBooked` entry per booking, written after the save, carrying the amount, the **masked** IBAN, the creditor account, the bank movement id and every Holded id posted. **A partial booking gets one too**, labelled `PARTIAL` — postings an admin (or the sweep) caused are never invisible to the Board.
- **An accepted posting is never thrown away over an unreadable response.** A 2xx means it posted, so `PayPurchaseDocumentAsync` returns `"unconfirmed:{documentId}"` (and `PostLedgerEntryAsync` `"unconfirmed:entry"`) instead of throwing when Holded gives back no readable id. The allocation continues and the transfer books normally; the sentinel lands in the audit entry and in `HoldedPaymentRefs`, naming the document a human must eyeball in Holded, and is never sent to Holded as a reconcile ref. Throwing would have lost the payment from the record entirely and left the transfer retryable.
- **The bank line drives booking, not a click.** `BookSepaTransferAsync` takes the bank movement id and re-validates the pairing server-side — amount, account where the line's text names one, not already reconciled, not already booked to another transfer, exactly one unbooked transfer matches, and no other line could have paid it — never trusting what was posted. **A textless line matches on amount and date alone**, Sabadell having dropped the remittance text: its date within the page's tolerance (days either side of the file date, default 2, editable per view and posted back with every booking) of the file's; two unbooked transfers of its amount, two such lines for one transfer, or an amount that is also a multi-transfer file's total, all refuse and go to the "needs a human" panel — except a **set**: N unbooked transfers of one amount in one file and exactly N textless lines of that amount in its window, with no other line that could be paying any of them, pair one to one (line by Holded id against transfer by creditor account; `TextlessSetPairs`). Which line paid which is unknowable and accounting-neutral, since each transfer books its own creditor account and the lines carry no text. Unequal counts, transfers across two files, or a not-bookable transfer in the set keep the whole set on the panel. The page judges ambiguity over every unbooked row, bookable or not, the same set the booking re-validates with. The sweep never books a textless match; `/Finance/Sepa` offers it only as a whole-file approval (`BookSepaFileMatchesAsync`), and only when every unbooked transfer in the file is matched. No unique DB index enforces "one line, one transfer" (a Process of a whole file is the one exception, below): the guard is the in-memory check plus a process-wide gate — every booking on this server runs one at a time, so the second caller reads the row *after* the first stamped it rather than both reading "not booked" and both posting the whole amount ([`audit-log-as-concurrency-safety-net`](../../../../memory/architecture/audit-log-as-concurrency-safety-net.md)). The sweep's `[DisableConcurrentExecution]` only keeps two sweeps apart; a Book click is not in that lock.
- **Booking posts the whole transfer, whatever the account owes now.** The file went to the bank and the money left, so the ledger has to show it: the live balance never caps or refuses a booking. An account that owed less than the transfer (two files from one balance, documents hand-paid in Holded in between) ends up in credit — the journal-entry remainder carries the excess, the booking audit entry says `OVERPAID` with what was owed and the resulting credit, and the next ledger sync shows the negative balance on `/Finance/Creditors`. Booked still never means less than the transfer's amount is in the ledger: when the row save fails after Holded accepted the postings, that is a `PARTIAL` audit entry and the row stays unbooked.
- **A line for a whole file is a human's call.** Files ask the bank for one debit per transfer (`BtchBookg` false), but a bank may still debit a multi-transfer file as one line. A line whose amount is **exactly** the file's total, dated no earlier than the file, untouched in Holded, naming no single transfer, and the only line to fit the only file that fits it, is shown on that file's card on `/Finance/Sepa` — with whether its text quotes the file's id — and nothing books it until a finance admin presses **Process** (`BookSepaFileAsync`). The sweep never does. Process re-validates the pairing, checks every transfer's binding before posting any, books each exactly as a single booking would, stamps each row with the one line's id, and reconciles the line once after the last. A refusal part-way leaves the booked rows booked; processing again books only the rest.
- **One stuck bank line cannot block the sweep forever** (peterdrier/Humans#1946). A *pending outgoing* Sabadell line with no readable date (a genuinely malformed line: until 2026-10-17 the client read a `date` key the feed does not have, so this fired on every normal line) fails the feed read closed, because dropping it could hide a rival of a transfer's amount (peterdrier/Humans#1861) — so the sweep books nothing. The sweep counts consecutive runs failing on the same Holded movement id (`CacheKeys.SepaStuckBankLines`, in a singleton `TrackedCache` shared by scoped booking owners and surfaced on `/Debug/CacheStats`: a restart only restarts the count, which fails closed a little longer). The first two runs still book nothing; the third (`StuckLineSkipAfterRuns`, two-hourly) skips that id, books the rest of the feed, logs a warning every run, and writes one `AuditAction.SepaBankLineSkipped` entry naming the line. Every other feed read (the booking's own re-read, `/Finance/Sepa`, the post-reconcile check) skips a given-up line too. A line that reads again, or a different stuck line, restarts the count from one. A skipped line is no longer counted as a rival — a human must fix or reconcile it in Holded.
- **Persist before reconcile.** The booking is saved to the row *before* the bank line is reconciled in Holded — the local save is the cheap write, and losing it after Holded already took the money was nobodies-collective/Humans#1185 itself. A reconcile that fails or is refused leaves `ReconciledAt` null and the booking still stands; the sweep re-issues it from the row's payment ids.
- **The reconcile names payment ids, never documents.** The bank line is reconciled against the payment ids Holded returned for the postings (`document_type: "payment"`). Naming a purchase document instead makes Holded mint a second payment for the whole line on it (production, 2026-10-17). An `unconfirmed:` ref is left out, never swapped for its document id; a journal entry is never sent, since Holded cannot link one to a bank line — such a payout stays reconcile-pending until a human reconciles it in Holded, and the booking says so. `ReconciledAt` is stamped on Holded's acceptance only when every euro of the line was named in one call; otherwise only when the line then reads `reconciled`. The ids go in one call first; a 4xx on a list longer than `ReconcileChunkSize` (50) retries the same ids in chunks of 50 against the same line and then reads the line back (`StampIfReconciledAsync`). A refused chunk does not stop the chunks behind it; the refused ids are logged at Warning, and because the chunks already through left the line `partial`, which the sweep never re-issues (it re-issues only a line still `pending`, through the same path), those ids are a human's to finish in Holded. A line still `partial` after a complete list went through is logged at Warning with the line id, amount and ids.
- **A retry after `PARTIAL` is refused.** Holded drops the description on a purchase payment, so the tag sum cannot see one; a run that posted payments and stopped short of booking writes their ids to `HoldedPaymentRefs`, the page shows why the row cannot be booked, and `Book` refuses. Settled in Holded by hand.

## Negative Access Rules

- Coordinators **cannot** view `/Finance/*` routes.
- FinanceAdmin, Board and UsersAdmin **cannot** reveal a raw IBAN or see the Payment Details card; both are Admin only.
- The sync job **cannot** delete `HoldedExpenseDoc` rows. Holded-side deletions are not handled in v1.
- Finance **cannot** read or write Budget tables directly, and **cannot write Budget data at all**: its only Budget dependency is the read contract `IBudgetService`.
- Finance **cannot** write to `holded_expense_docs` outside the sync job. No manual create/edit/delete UI for expense docs in v1.
- Finance **cannot** read Expenses tables: what it needs for the duplicate check and the reconciliation (the lines and the values the push writes) comes through `IExpenseHoldedBookingService` as DTOs.
- The expense reconciliation **cannot** call Holded or write anything: page, backdoor route, nightly job and Creditors banner all read local mirrors.
- Apply **cannot** delete a document some expense line links to, a document with a payment, a draft, or a bound contact.

## Triggers

- None on the budget side: this section only reads Budget, so it fires no Budget-side effects.
- On **SEPA payout generation**: after the file and its transfers are saved and audited, one `sepa_payout_generated` email per transfer goes to the bound member (`FinanceEmails.SepaPayoutGenerated`, `MessageCategory.System`, in their supported preferred language, with English fallback for blank, malformed or unsupported preferences) naming the amount and the masked IBAN. A refused batch sends nothing; a member with no notification email is logged and skipped. Recipient lookup and individual email failures are logged without blocking download of the saved file or suppressing later recipients; notifications finish independently of request cancellation after the save. Booking sends nothing — by then the money has moved (peterdrier/Humans#1820).
- On **IBAN reveal** (`/Finance/Admin/Members/{id}/RevealIban`): one `AuditAction.IbanReveal` entry, entity `User` = the member, actor = the admin. Nothing is written when no IBAN is on record.
- On **SEPA transfer withdrawal**: one `sepa_payout_withdrawn` email to the member (`FinanceEmails.SepaPayoutWithdrawn`, same language rules) saying the transfer announced on the file's date was cancelled before execution and no money moved.
- The internal `SepaPayoutEmails` owns payout-generation and withdrawal email delivery, including recipient lookup, language fallback and best-effort failure handling. The owner service retains accounting writes, audit and post-commit dispatch ordering; no DI registration or contract is added.
- On every **document sync**: one notes GET per `ER: ` document new to the mirror, capped at 50 a run and ended by the first transient failure (failures logged as one Warning count, never failing the sync). Then the duplicate check runs over the documents just pulled and logs one line — Warning with line, document, amount and member counts when any are found, Information "zero" otherwise. A failure of the check is logged at Error and never fails the sync.
- Status reads (`GetDocSyncInfoAsync` and the connector overview) never insert sync state. Before the first sync they return an unsaved Idle default; `SaveDocSyncStateAsync` owns singleton creation and updates.
- When the sync job starts, `HoldedDocSyncState.Status` flips to `Running`. On success returns to `Idle` with `LastSyncAt` and `LastSyncedDocCount` updated. On exception goes to `Error` with `LastError` bounded to its 2000-unit column without splitting UTF-16 surrogate pairs; the original exception is logged and rethrown, and the next scheduled run retries. Caller-aborted syncs log a stack-free warning and rethrow cancellation; dependency failures retain error logs with exceptions.

## Cross-Section Dependencies

Derived from `Humans.Finance.csproj`'s project references and the contracts namespaces the service uses (Budget, Holded, GDPR and AuditLog are referenced as section projects, whose public face is their `Contracts/` folder; Users and Email through their `.Contracts` leaves):

- **Budget** (`Humans.Budget.Contracts`): `IBudgetService.GetActiveYearAsync`, for the categories the provisioning plan is built from and the plan cash flow projects. Read-only.
- **Tickets** (`Humans.Tickets.Contracts`): `ITicketService.GetTicketOrdersAsync`, the paid orders whose gross revenue seeds the cash-flow runway. Read-only.
- **Holded** (`Humans.Holded.Contracts`): `IHoldedService` for cached ledger lines and account balances, and `IHoldedClient` for the live contact/account calls the provisioning and bind paths make.
- **Users** (`Humans.Users.Contracts`): `IUserService.GetUserInfosAsync`, to name bound members on `/Finance/Creditors` and to address the payout email; `GetUserInfoAsync` / `GetAllUserInfosAsync` for payment details (merge-chain resolution and the legacy `ProfileInfo.Iban` fallback and copy); `IUserMerge`, implemented by `PaymentDetailsService`; `IUserEmailService.GetNotificationTargetEmailsAsync` for the address itself.
- **Expenses** (`Humans.Expenses.Contracts`): `IExpenseHoldedBookingService.GetHoldedBookingsAsync`, the linked expense lines with the description, notes and booked amount the push wrote, for the duplicate-document check, and `GetHoldedReconLinesAsync`, every expected document with its outbox state, for the expense reconciliation. Read-only; Expenses already depends on Finance's `.Contracts` leaf, so the project reference goes to the Expenses section project. The contract is kept apart from `IExpenseReportService` because `ExpenseReportService` takes `IHoldedFinanceService`: its implementation needs only the Expenses repository and `IUserService`, so the two services never resolve each other.
- **Email** (`Humans.Email.Contracts`): `IEmailService.SendAsync` for the payout email. The template, its resx set (`FinanceResource`, the section's only one — its screens are admin-only and carry no keys) and its `/Email/EmailPreview` sample are this section's (`FinanceEmails`, `FinanceEmailPreviews`); Email keeps the mechanics (memory/architecture/email-templates-live-in-sender.md).
- **GDPR** (`Humans.Gdpr.Contracts`): Finance implements `IUserDataContributor` for the Article 15 export of every creditor binding and SEPA payout across the member's resolved `AllUserIds` (including archived merge sources; retained financial rows are not rekeyed). `HoldedCreditorAccount` preserves its original object-or-null shape for the requested account; `AdditionalHoldedCreditorAccounts` contains other merge-chain bindings with their user ids, omitted when none exist. Payout rows remain globally ordered by generation time. `CreditorLedger` carries the mirrored Holded journal lines (date, entry, type, free-text description, debit, credit, running balance) of every account those bindings resolve to, read through `GetCreditorLedgerAsync` like the creditor statement; an account also bound to a member outside the requester's merge chain (a collision) is withheld with a warning log until resolved on `/Finance/Creditors`, since the lines carry no user id; null when unbound, all accounts withheld, or the mirror holds no lines. Generation, booking and reconciliation dates serialize as UTC ISO 8601 strings; absent booking/reconciliation dates stay null. For Article 17 erasure, `EraseForUserAsync` drops the binding (`ClearCreditorContactAsync`); `ErasureDeclaration` maps both creditor-binding slices to `null` (erased in full) and `SepaPayouts` and `CreditorLedger` to a fiscal-retention basis (Código de Comercio Art. 30, Ley 58/2003 Art. 66 — GDPR Art. 17(3)(b)): a payment order stripped of its payee is no longer evidence of the payment. Ledger lines carry no user id, so once the binding is erased they are no longer linkable to the person here. Financial records are never erased for GDPR reasons ([`financial-records-never-erased`](../../../../memory/architecture/financial-records-never-erased.md)). The invoices themselves live in Holded and are fiscal records outside this section's ownership. `PaymentDetailsService` is a second contributor: slice `PaymentDetails` (the masked IBAN), erased in full by deleting the row; the legacy `profiles.Iban` is erased by Users.

Budget never calls into Finance.

## Architecture

**Status:** (A) — own project (`src/Sections/Humans.Finance` + `src/Sections/Humans.Finance.Contracts`), own service, owned repository, own `FinanceDbContext`.

**Bank-booking owner:** Internal `SepaBankBooking` owns bank-line matching, posting, reconciliation and the recurring sweep through Finance’s own repository. `Service` delegates its existing booking/read methods to that owner; contracts and the Hangfire target stay unchanged. `SepaOptions.GetPayoutSettings` owns the shared configuration gate for payout generation and booking.

**Owning service:** `Service` (`Humans.Finance.Services`), implements the internal `IHoldedFinanceManagement` (`Services/IHoldedFinanceManagement.cs`), which composes two faces published from the contracts leaf. `IHoldedFinanceService` is the read-only surface for cross-section callers (`BudgetAdminController`, `Expenses.ExpensesController`, `Preapprovals`, `Backdoor.BackdoorFinanceController`); `IHoldedFinanceWrite` is the narrow write surface (the Expenses publisher for the creditor-contact calls, `Workgroups.WorkgroupService` for the expense-account calls). Finance's own `FinanceController` and `HoldedDocSyncJob` inject `IHoldedFinanceManagement`, which adds the internal `SyncAsync` (the purchase-doc sync; no other section triggers it).
**Cash flow:** `CashFlowService` (internal, scoped, no repository) owns bucketing, running net and runway; reads Budget and Tickets contracts. `VatCalculation` in Finance.Contracts owns VAT-inclusive portion and quarterly settlement-date math, shared with Budget summaries and category projections.
**Pure matcher:** `HoldedMatcher` (static, no dependencies)
**Pure file builder:** `SepaPaymentFileBuilder` with `SepaSchema` and `SepaText` (no IO, no clock, no configuration)
**Owned repository:** `IHoldedRepository` / `Repository` (`Humans.Finance.Data`) — tables under [Owned repository](#owned-repository)
**Document-sync body:** Internal `HoldedDocumentSync` owns purchase-document fetching, matching, mirror writes and sync-state transitions. `Service.SyncAsync` (internal `IHoldedFinanceManagement`) delegates to it; the facade retains provisioning, creditor and payout operations.

**Jobs:** `HoldedDocSyncJob` (internal, `Jobs/`, job id `holded-doc-sync`, nightly at 02:45, scheduled in `Section.cs`) runs `IHoldedFinanceManagement.SyncAsync`; with no `HOLDED_API_KEY_V2` it skips with an Information log. A failure records `Error` on the doc-sync state and fails the job. It runs ahead of Holded's own 03:00 ledger sweep (`HoldedSyncJob`, Holded's) to keep the two off the API at once, and both take the shared `holded-api` Hangfire job lock so a long doc pull still never overlaps the sweep; neither reads the other's output. The manual trigger is `POST /Finance/HoldedSync/Run` ("Sync Now" on `/Finance/HoldedUnmatched`). `SepaBankBookingJob` — see `ISepaBankBooking` below. `ExpenseReconJob` (internal, job id `expense-recon`, nightly at 03:30, after the doc sync and the ledger sweep) computes the all-years reconciliation and logs one line: Information `Expense recon: 0 problems — …` when clean, Warning with the counts otherwise (or when the mirror never synced). Local reads only, so it takes no `holded-api` lock.
**Migrations:** `Data/Migrations/`, on `FinanceDbContext` with its own history table `__EFMigrationsHistory_Finance`.
**Architecture tests:** `tests/Humans.Finance.Tests/FinanceArchitectureTests.cs`

### Public contract

| Interface | Methods | Notes |
|---|---|---|
| `IHoldedFinanceService` (read) | [`IHoldedFinanceService.cs`](../../Humans.Finance.Contracts/IHoldedFinanceService.cs) | Consumed by `BudgetAdminController` (actuals), `ExpensesController` (creditor status, ledger, account list), Preapprovals (`ListExpenseAccountsAsync`), `HoldedController` (`GetDocSyncInfoAsync`) and `Backdoor.BackdoorFinanceController` (peterdrier/Humans#1838: the same account/ledger reads, plus `GetCategoryMapAsync`, `GetSepaTransfersAsync` below and `GetExpenseReconAsync`, the expense reconciliation). |
| `IHoldedFinanceWrite` | [`IHoldedFinanceWrite.cs`](../../Humans.Finance.Contracts/IHoldedFinanceWrite.cs) | `EnsureCreditorContactAsync` / `SetCreditorAccountNumAsync` (Expenses), `CreateOrLinkExpenseAccountAsync` / `SetExpenseAccountActiveAsync` (Workgroups) — resolve, and retire/restore, an expense account for a caller with no budget category, the managed registry. |
| `ISepaBankBooking` | [`ISepaBankBooking.cs`](../../Humans.Finance.Contracts/ISepaBankBooking.cs) | `RunAsync` — the two-hourly SEPA bank-line sweep as one call. `SepaBankBookingJob` is the Hangfire shim; nothing else calls it. |
| `IFinanceService` (read) | [`IFinanceService.cs`](../../Humans.Finance.Contracts/IFinanceService.cs) | `GetIbanAsync` — Expenses (submit snapshot, the masked `/Expenses/{id}/Iban` page, the index's has-IBAN flag). |
| `IFinanceWrite` | [`IFinanceWrite.cs`](../../Humans.Finance.Contracts/IFinanceWrite.cs) | `SetIbanAsync` — Expenses' `/Expenses/{id}/Iban`. The caller validates and audits (`IbanSet` / `IbanRemove`), because only it knows on whose behalf. |
| `IPaymentDetailsManagement` | internal | `RevealIbanAsync` and the legacy copy, for `PaymentDetailsAdminController` and `ISectionDataMigrations`. |
| `IHoldedFinanceManagement` | internal | Finance's own full surface (`IHoldedFinanceService` + `IHoldedFinanceWrite` + `SyncAsync`), injected by `FinanceController` and `HoldedDocSyncJob`. |

`GetCategoryMapAsync` and `GetSepaTransfersAsync` (added for peterdrier/Humans#1838) expose, read-only, exactly what `/Finance/Holded`'s category-map table and `/Finance/Sepa` already render: `GetCategoryMapAsync` returns the same `HoldedCategoryMapRow` projection `GetConnectorOverviewAsync` builds (both share one private helper, so the two views cannot drift), and `GetSepaTransfersAsync` shares `GetSepaPayoutsAsync`'s pre-feed booking-state logic (`GetSepaPayoutRowsWithReasonsAsync`) but skips the live Holded bank-feed read — it exists only to offer `/Finance/Sepa`'s own "book this" button, and Backdoor never projects the candidate-bank-line/unmatched-movement fields it would fill. Both DTOs (`HoldedCategoryMapRow`, `SepaPayoutTransferRow`) live in the contracts leaf now, not this section's `Models/`.

**Controllers.** `/Finance` is served by two controllers under one route prefix: this section's `FinanceController` — the Holded, creditor, SEPA-payout and cash-flow surface, the routing table above — and `Humans.Budget.Controllers.BudgetAdminController`, Budget CRUD under the same `[Route("Finance")]`. See [`Budget.md`](../../Humans.Budget/Docs/Budget.md) for the Budget side.

**The Holded connector is not this section.** `IHoldedClient`, `HoldedClient`, `HoldedClientOptions`, `HoldedApiException` and the connector DTOs belong to the **Holded** section — public in its `Contracts/` folder (namespace `Humans.Holded.Contracts`), implementation `internal` in `Humans.Holded/Services/` — with their own [`Holded-connector.md`](../../Humans.Holded/Docs/Holded-connector.md). Finance consumes them through that folder. Consequence for the boundary: `HoldedCreditorLedger.Lines` carries Finance's own `CreditorLedgerLine` rather than the connector's `HoldedLedgerLineDto`, because Finance's contracts leaf may reference only the bottom of the graph and re-exporting another component's wire DTO across a section boundary is the thing the split exists to stop.

**Table names.** `holded_*` tables under a section called `Finance`, and a `FinanceDbContext` naming the live `__EFMigrationsHistory_Finance` table. The mismatch is real and deferred wholesale to nobodies-collective/Humans#1012 — a G5 move changes files, never the schema.

**Resources.** The `/Finance/*` pages are admin-only and English-only — no localization keys ([`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md)). `FinanceResource` exists for the one member-facing string set, the SEPA payout email (see Triggers).

### Feature 2 — creditor reads over the Holded section's mirror

The ledger cache and its sync are the Holded section's (full mirror, all accounts, replace semantics, balance reconciliation — see `src/Sections/Humans.Holded/Docs/Holded.md`). Finance derives creditor status/statements from `IHoldedService.GetLedgerLinesAsync` / `GetAccountBalancesAsync`, range-filtered to the `40000000`–`41999999` creditor block on Finance's side. Sync buttons live on `/Holded`. Page loads still cost **zero Holded calls per view**; the admin creditor overview additionally reads the cached Holded contact list for account names and makes one live purchase-document list call for its "Open docs" column — see Invariants.

The Expenses section reads creditor status via `GetCreditorStatusAsync(supplierAccountNum)` and the statement via `GetCreditorLedgerAsync(supplierAccountNum)`. Both derive from the cached lines: balance = Σdebit − Σcredit (balance ≥ 0 = settled), owed = max(0, −balance), payments = debit lines. The debit lines stay internal to the derivation; only the aggregates (`TotalPaid`, `LastPaymentDate`) leave the service.

### Feature 3 — SEPA payout of creditor balances

`/Finance/Creditors` doubles as the payout screen: tick the payable rows, adjust amounts, and
`POST /Finance/Sepa/Generate` streams a Norma 34-14 / pain.001.001.09 file for Sabadell's "Enviar
ficheros". It reads the same balances and the same cached contact list the page already shows — no
extra Holded call — and writes only its own two tables plus one audit entry per transfer, then
emails each paid member that the transfer is on its way.

`/Finance/Sepa` closes the loop once the file is with the bank: it lists every generated file with
its transfers and books each one's payment into Holded. Full spec:
[`Docs/features/sepa-payout.md`](features/sepa-payout.md).

**Org-accounting boundary (HARD):** Humans writes to Holded's books only when booking a SEPA
transfer, and only two kinds of posting: a **payment against a purchase document it can name**
(`POST /purchases/{id}/payments`), and **one journal entry for whatever those payments do not cover**
— debit the member's creditor account, credit the treasury, dated the bank line and tagged with the
transfer. It never writes debt-reassignment entries, never posts a journal entry outside a booking,
and never modifies the chart of accounts to reflect internal transfers. `holded_ledger_lines` is a read-through cache of
immutable journal facts, not a ledger Humans writes to — a booked payment reaches it the ordinary way,
on the next sync.

### Owned repository

- **`IHoldedRepository`** — owns `holded_expense_docs`, `holded_category_map`, `holded_managed_accounts`, `holded_doc_sync_state`, `holded_creditor_contacts`, `sepa_payout_files`, `sepa_payout_transfers`
  - No cross-domain navs: `BudgetCategoryId`, `HoldedCreditorContact.UserId`, `SepaPayoutFile.GeneratedByUserId` and `SepaPayoutTransfer.UserId` are FK-only, no navigation property
  - A payout file and its transfers are written in one `AddSepaPayoutAsync` save — one without the other is not a state this section wants to be in
  - `SaveSepaTransferWithdrawnAsync` is the one update outside the booking columns: `WithdrawnAt`/`WithdrawnByUserId` only
  - Expense docs upsert (full overwrite on re-sync); ledger tables belong to the Holded section
- **`IPaymentDetailsRepository`** — owns `finance_payment_details`; `UserId` is the key and a bare id

### Touch-and-clean guidance

- **Soft boundary:** `TicketingProjection` and `TicketingBudgetService` are conceptually "actuals materialization" but live in Budget today. Treat as known soft boundary — separate cleanup, not an active violation.
