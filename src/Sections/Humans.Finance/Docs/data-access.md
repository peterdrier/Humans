# Finance — Data Access

## Finance

Folder: `src/Sections/Humans.Finance/Services/` (namespace
`Humans.Finance.Services`). **DbContext:**
`FinanceDbContext`.
`Repository` (`src/Sections/Humans.Finance/Data/Repository.cs`, implements
`IHoldedRepository`) injects `IDbContextFactory<FinanceDbContext>` directly
— a distinct context from `ExpensesDbContext` and from
`HoldedDbContext`. Owns `HoldedExpenseDocs`,
`HoldedCategoryMap`, `HoldedDocSyncStates`, `HoldedCreditorContacts`,
`HoldedManagedAccounts`, `SepaPayoutFiles`, `SepaPayoutTransfers`.
`PaymentDetailsRepository` (`Data/PaymentDetailsRepository.cs`, implements
`IPaymentDetailsRepository`) owns `PaymentDetails` (`finance_payment_details`) on the same context.

The ledger mirror (`HoldedLedgerLines` and its sync state, chart-of-accounts
cache, and API call-metering log) is owned by the
[Holded](../../Humans.Holded/Docs/data-access.md) section
(`IHoldedMirrorRepository` / `HoldedDbContext`), not Finance. Finance owns
Holded-account **provisioning** (mapping budget categories to Holded
expense accounts, `HoldedCategoryMap`), purchase-**document** sync and
category/tag matching (`HoldedExpenseDocs`, via `HoldedMatcher`), and the
creditor-**contact binding** surface (`HoldedCreditorContacts` — user ↔
Holded supplier-account bindings, including an at-most-one-member
collision guard).

Sync-state reads return the persisted singleton or an unsaved Idle default; only `SaveDocSyncStateAsync` inserts or updates `HoldedDocSyncStates`.

### SepaBankBooking (section-internal)

Composed by `Service` using `IHoldedRepository`, `IHoldedClient`, `IClock`,
a singleton `TrackedCache<string, Dictionary<string, int>>` for sweep failure
counts, `IAuditLogService` and `IOptions<SepaOptions>`. Owns the manual
and scheduled bank-booking workflow: reads creditor bindings and payout rows,
updates booking/payment-reference/reconciliation columns on `SepaPayoutTransfers`,
and calls Holded through its contract. Its shared booking gate covers all entry
points. No provisioning, Budget or email dependency.

### HoldedDocumentSync (section-internal)

Composed by `Service` using `IHoldedRepository`, `IHoldedClient`, `IClock`
and the facade's logger. Owns purchase-document fetching and matching,
`HoldedExpenseDocs` snapshot replacement and `HoldedDocSyncStates` writes. Reads the
category map and managed-account registry; no Budget, user or email dependency.
The internal `IHoldedFinanceManagement.SyncAsync` entry point delegates here; its callers are Finance's own `HoldedDocSyncJob` (nightly) and `FinanceController.RunHoldedSync` (manual), never another section. The facade passes its duplicate-document check in, run over the fetched documents before the snapshot is written (it reads `IExpenseHoldedBookingService`, which the sync itself never does).

### CashFlowService (Scoped, section-internal)

No repository, no tables, no cache. Computes `/Finance/CashFlow` from
`IBudgetService.GetActiveYearAsync` (the plan tree, including each line item's
`VatRate` and `ExpectedDate`) and `ITicketService.GetTicketOrdersAsync` (paid
orders' gross revenue for the runway). Injected by `FinanceController` only.

### Service (Scoped)

Repository: `IHoldedRepository`.

| Table | R/W |
|-------|-----|
| HoldedCategoryMap | R/W |
| HoldedExpenseDocs | R/W |
| HoldedCreditorContacts | R/W (creditor-contact bindings per user) |
| HoldedDocSyncStates | R/W |
| SepaPayoutFiles | R/W (append-only writes; read joined for the Article 15 export and for `/Finance/Sepa`) |
| SepaPayoutTransfers | R/W (appended at generation, `HoldedContactId` captured from the binding at that point; the booking columns are updated only by the composed `SepaBankBooking` owner — `SaveSepaTransferBookingAsync` (`BookedAt`/`BookedByUserId`/`HoldedBankMovementId`/`ReconciledAt`/`HoldedPaymentRefs`), `SaveSepaTransferPaymentRefsAsync` (`HoldedPaymentRefs` only, a run that posted and then stopped), `MarkSepaTransferReconciledAsync` (`ReconciledAt` only, a later reconcile pass); the one other update is `SaveSepaTransferWithdrawnAsync` (`WithdrawnAt`/`WithdrawnByUserId` only, a transfer the bank cancelled, written by the facade's `WithdrawSepaTransferAsync`). Read per user for the Article 15 export and flattened with the file for `/Finance/Sepa`) |

Cross-section calls via `IBudgetService` (`budget` in the ctor), `IHoldedService` (the Holded section's
ledger-mirror read surface — `holded` in the ctor; ledger-line /
account-balance reads for creditor status, ledger, and account listing),
`IHoldedClient` (Holded's `Contracts/` folder — purchase-document / contact / expense-account
API calls, plus `PayPurchaseDocumentAsync` for SEPA booking), `IAuditLogService`
(one entry per SEPA credit transfer generated, and one per transfer booked), and
`IUserService` / `IUserEmailService` / `IEmailService` (the payout-generated and
payout-withdrawn notification to each creditor, through the section's `FinanceEmails` builder).
Implements `IHoldedFinanceManagement` (the public `IHoldedFinanceService` and `IHoldedFinanceWrite`), `IHoldedFinanceAdminService`
and `IUserDataContributor`.
Injects `IMemoryCache` for the 2-minute Holded contact-list entry
(`CacheKeys.HoldedContacts`) and `IOptions<SepaOptions>` for the organisation's
SEPA identity.

`IHoldedFinanceAdminService` (`Services/IHoldedFinanceAdminService.cs`) is
**internal** — the `/Finance/Holded` connector index is this section's own
screen, so its read model is not cross-section surface; the same shape as the
Holded section's `IHoldedAdminService`. `GetConnectorOverviewAsync` reads `HoldedDocSyncStates`,
`HoldedCategoryMap`, `HoldedExpenseDocs` (via `GetAllDocsAsync` — the
unmatched and matched-for-year reads are each a filtered slice and neither
composes into "all docs") and `HoldedCreditorContacts`, plus
`IBudgetService` for category names. **No `IHoldedClient` call** — the
index must not inherit the connector's 30 s timeout
(nobodies-collective/Humans#976, #1000). `GetProvisioningPlanAsync` /
`ProvisionAsync` (`/Finance/HoldedAccounts`) and `SetCreditorContactAsync` /
`ClearCreditorContactAsync` (Bind and Unbind on `/Finance/Creditors`) live here
too: no other section calls them. The SEPA methods,
`GetSepaPayoutSettings`, `GenerateSepaPayoutAsync`, `GetSepaPayoutsAsync` and
`BookSepaTransferAsync(transferId, bankMovementId, actorUserId)`, serve
`/Finance/Creditors`' payout column, `POST /Finance/Sepa/Generate`,
`GET /Finance/Sepa` and `POST /Finance/Sepa/Book` — also this section's own
screens, so also not cross-section surface. `BookSepaTransferAsync` is marked
`[ExternalWrite]` and takes no `CancellationToken`: it posts payments to Holded
and has to finish once it has started
([`cancellation-token-propagation`](../../../../memory/architecture/cancellation-token-propagation.md)).
It calls the Holded section's `IHoldedClient.ListBankMovementsAsync` (find and
match the bank line), `ListAccountingAccountsAsync` (live balance),
`ListLedgerEntriesAsync` (what is already posted, by tag) and
`ReconcileBankMovementAsync` (nobodies-collective/Humans#1185), alongside the
existing `PayPurchaseDocumentAsync` / `PostLedgerEntryAsync` / `ListPurchaseDocumentsAsync`.
`WithdrawSepaTransferAsync`, `GetHoldedDuplicatesAsync` and `RemoveHoldedDuplicatesAsync`
(`[ExternalWrite]`, no token) serve `POST /Finance/Sepa/Withdraw` and
`/Finance/HoldedDuplicates`; the duplicate check reads the linked expense lines
through `IExpenseHoldedBookingService.GetHoldedBookingsAsync` (Expenses, DTOs only) and
deletes through `IHoldedClient.DeletePurchaseDocumentAsync` / `DeleteContactAsync`.

`ISepaBankBooking.RunAsync` (`Humans.Finance.Contracts`, public — the Hangfire
job needs a public concrete type but must not take the internal
`IHoldedFinanceAdminService`, the same seam `IHoldedNightlySync` is for Holded's `HoldedSyncJob`) is
the bank-line sweep: books every unbooked transfer whose Sabadell line has
appeared, and retries the reconcile for every booked-but-unreconciled one.
Also `[ExternalWrite]`. `SepaBankBookingJob` (`Jobs/`) is its Hangfire shim,
registered in `Section.cs`, cron `17 */2 * * *`.

### PaymentDetailsService (Scoped)

Repository: `IPaymentDetailsRepository`.

| Table | R/W |
|-------|-----|
| PaymentDetails (`finance_payment_details`) | R/W — one row per member, keyed by `UserId` (bare id, no FK). A null `Iban` records a removal. |

Implements the public `IFinanceService` (`GetIbanAsync`) and `IFinanceWrite` (`SetIbanAsync`), both
called by Expenses; the internal `IPaymentDetailsManagement` (`RevealIbanAsync` for
`PaymentDetailsAdminController`, and the `finance.iban-to-payment-details` copy for
`ISectionDataMigrations`); `IUserDataContributor` (slice `PaymentDetails`, masked IBAN; erasure
deletes the row) and `IUserMerge` (the survivor's IBAN wins, else the source's row moves).
Cross-section calls: `IUserService` (merge-chain resolution, the user-exists check, and the
`[Obsolete]` `ProfileInfo.Iban` read fallback until the copy is done in production) and
`IAuditLogService` (`IbanReveal`). Uncached: each read is one keyed row.

`PaymentDetailsViewComponent` (the Payment Details card, contributed to `/Users/Admin/{id}` by
`IUserPart`) reads through `IFinanceService`.

### SepaPaymentFileBuilder / SepaText / SepaSchema

Pure static helpers — pain.001.001.09 assembly, SEPA character folding and
XSD validation against the embedded official schema. No DI dependencies, no
DB access, no IO.

### HoldedMatcher

Stateless matcher — pairs Holded docs against budget categories. No DI
dependencies beyond pure data shaping, no DB access.

---


