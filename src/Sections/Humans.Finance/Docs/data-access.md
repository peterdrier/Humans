# Finance — Data Access

## Finance

Folder: `src/Sections/Humans.Finance/Services/` (namespace
`Humans.Finance.Services`). **DbContext:**
`FinanceDbContext`.
`Repository` (`src/Sections/Humans.Finance/Data/Repository.cs`, implements
`IHoldedRepository`) injects `IDbContextFactory<FinanceDbContext>` directly
— a distinct context from `ExpensesDbContext` and from
`HoldedDbContext`. Owns
`HoldedCategoryMap`, `HoldedCreditorContacts`,
`HoldedManagedAccounts`, `SepaPayoutFiles`, `SepaPayoutTransfers`. `HoldedExpenseDocs` and
`HoldedDocSyncStates` are still mapped but unread and unwritten since the purchase-document mirror
moved to Holded; dropping them is its own PR (`no-drops-until-prod-verified`).
`PaymentDetailsRepository` (`Data/PaymentDetailsRepository.cs`, implements
`IPaymentDetailsRepository`) owns `PaymentDetails` (`finance_payment_details`) on the same context.

The ledger mirror (`HoldedLedgerLines` and its sync state, chart-of-accounts
cache, and API call-metering log) and the purchase-document mirror are owned by the
[Holded](../../Humans.Holded/Docs/data-access.md) section
(`IHoldedMirrorRepository` / `HoldedDbContext`), not Finance. Finance owns
Holded-account **provisioning** (mapping budget categories to Holded
expense accounts, `HoldedCategoryMap`), category/tag **attribution** of the
purchase documents Holded mirrors (`HoldedMatcher`, computed on read), and the
creditor-**contact binding** surface (`HoldedCreditorContacts` — user ↔
Holded supplier-account bindings, including an at-most-one-member
collision guard).


### SepaBankBooking (section-internal)

Composed by `Service` using `IHoldedRepository`, `IHoldedClient`, `IClock`,
a singleton `TrackedCache<string, Dictionary<string, int>>` for sweep failure
counts, `IAuditLogService` and `IOptions<SepaOptions>`. Owns the manual
and scheduled bank-booking workflow: reads creditor bindings and payout rows,
updates booking/payment-reference/reconciliation columns on `SepaPayoutTransfers`,
and calls Holded through its contract. Its shared booking gate covers all entry
points. No provisioning, Budget or email dependency.

### CashFlowService (Scoped, section-internal)

No repository, no tables, no cache. Computes `/Finance/CashFlow` from
`IBudgetService.GetActiveYearAsync` (the plan tree, including each line item's
`VatRate` and `ExpectedDate`) and `ITicketService.GetTicketOrdersAsync` (paid
orders' gross revenue for the runway). Injected by `FinanceController` only.

### FinanceOverviewService (Scoped, section-internal)

No repository, no tables, no cache. Composes `/Finance` from the Holded ledger
mirror (`GetLedgerSyncedAtAsync`, `GetAccountBalancesAsync`, `GetChartAsync`,
`GetLedgerLinesAsync` for the 472/477/4751 tax accounts), this section's
`ListCreditorAccountsAsync`, `GetExpenseReconAsync` and `GetActualsForYearAsync`,
`IExpenseReportService.GetReviewQueueAsync`, `IBudgetService`,
`CashFlowService` and `IPleoService.GetWalletBalanceAsync` (the Pleo card).
Injected by `FinanceController` only.

### PleoBooking (Scoped, section-internal) · PleoBookingState, PleoBookingRunner (Singleton)

Repository: `IHoldedRepository`, for `PleoBookingHolds` only. No cache. Orchestrates Pleo → Holded: reads
`IPleoService.GetSettledEntriesAsync` / `GetWalletBalanceAsync`, the 57200003
and 57200001 lines, balances and chart through `IHoldedService`, re-pulls 57200003
with `IHoldedService.SyncAccountLedgerAsync` before and after a run, and posts
through `IHoldedWrite.PostJournalEntryAsync`. `PleoBookingState` holds the
current/last run; `PleoBookingRunner` runs `BookAsync` in its own scope off the
request. Injected by `FinanceController` only.

### CreditorMatchService (Scoped, section-internal)

No repository, no tables, no cache. Matches Holded creditor ledger lines against
expense reports for `/Finance` via `IHoldedService.GetLedgerLinesAsync` and
`IExpenseReportService.GetReviewQueueAsync`. Injected by `FinanceController` only.

### Service (Scoped)

Repository: `IHoldedRepository`.

| Table | R/W |
|-------|-----|
| HoldedCategoryMap | R/W |
| HoldedCreditorContacts | R/W (creditor-contact bindings per user) |
| SepaPayoutFiles | R/W (append-only writes; read joined for the Article 15 export and for `/Finance/Sepa`) |
| SepaPayoutTransfers | R/W (appended at generation, `HoldedContactId` captured from the binding at that point; the booking columns are updated only by the composed `SepaBankBooking` owner — `SaveSepaTransferBookingAsync` (`BookedAt`/`BookedByUserId`/`HoldedBankMovementId`/`ReconciledAt`/`HoldedPaymentRefs`), `SaveSepaTransferPaymentRefsAsync` (`HoldedPaymentRefs` only, a run that posted and then stopped), `MarkSepaTransferReconciledAsync` (`ReconciledAt` only, a later reconcile pass); the one other update is `SaveSepaTransfersWithdrawnAsync` (`WithdrawnAt`/`WithdrawnByUserId` only, a transfer the bank cancelled or a whole rejected file, written by the facade's `WithdrawSepaTransferAsync` and `CancelSepaFileAsync`). Read per user for the Article 15 export and flattened with the file for `/Finance/Sepa`) |

Cross-section calls via `IBudgetService` (`budget` in the ctor), `IHoldedService` (the Holded section's
mirror read surface — `holded` in the ctor; ledger-line /
account-balance reads for creditor status, ledger, and account listing, and the purchase-document
mirror for actuals, the unmatched queue and expense recon),
`IHoldedClient` (Holded's `Contracts/` folder — purchase-document / contact / expense-account
API calls, plus `PayPurchaseDocumentAsync` for SEPA booking), `IAuditLogService`
(one entry per SEPA credit transfer generated, and one per transfer booked), and
`IUserService` / `IEmailService` (the payout-generated and
payout-withdrawn notification to each creditor, through the section's `FinanceEmails` builder).
Implements the public `IFinanceService` and `IFinanceWrite` (their IBAN members delegate to
`IPaymentDetailsManagement`), the internal `IFinanceAdminService` and `IUserDataContributor`.
Injects `IMemoryCache` for the 2-minute Holded contact-list entry
(`CacheKeys.HoldedContacts`) and `IOptions<SepaOptions>` for the organisation's
SEPA identity.

`IFinanceAdminService` (`Services/IFinanceAdminService.cs`) is
**internal** — Finance's own screens' read models, not cross-section surface.
`GetManagedAccountsAsync` (`/Finance/HoldedAccounts`) reads `HoldedManagedAccounts`.
`GetProvisioningPlanAsync` /
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
existing `PayPurchaseDocumentAsync` / `ListPurchaseDocumentsAsync`. Journal remainders use
`IHoldedWrite.PostJournalEntryAsync`, returning a confirmation outcome rather than a connector reference.
`WithdrawSepaTransferAsync`, `CancelSepaFileAsync`, `GetHoldedDuplicatesAsync` and `RemoveHoldedDuplicatesAsync`
(`[ExternalWrite]`, no token) serve `POST /Finance/Sepa/Withdraw`, `/Finance/Sepa/CancelFile` and
`/Finance/HoldedDuplicates`; the duplicate check reads the linked expense lines
through `IExpenseHoldedBookingService.GetHoldedBookingsAsync` (Expenses, DTOs only) and
deletes through `IHoldedClient.DeletePurchaseDocumentAsync` / `DeleteContactAsync`.
`GetExpenseReconAsync` (public, `IFinanceService`; `/Finance/ExpenseRecon`, the
`/Finance/Creditors` banner, `ExpenseReconJob` and Backdoor) reads `HoldedCreditorContacts` and `SepaPayoutTransfers`, plus
`IExpenseHoldedBookingService.GetHoldedReconLinesAsync`, `IHoldedService.GetLedgerSyncedAtAsync`,
`GetAccountBalancesAsync` and `GetLedgerLinesAsync` (every creditor-range account), the cached `IHoldedClient.ListContactsAsync`
that `/Finance/Creditors` also reads (which account each bound contact is on), and
`IUserService.GetUserInfosAsync`. **No write.**

`RunSepaBookingSweepAsync` (also `[ExternalWrite]`) is the bank-line sweep: books every unbooked
transfer whose Sabadell line has appeared, and retries the reconcile for every booked-but-unreconciled
one. `SepaBankBookingJob` (`Jobs/`, internal, on `IFinanceAdminService`) is its Hangfire shim, registered in `Section.cs`, cron
`17 */2 * * *`.

### PaymentDetailsService (Scoped)

Repository: `IPaymentDetailsRepository`.

| Table | R/W |
|-------|-----|
| PleoBookingHolds (`pleo_booking_holds`) | R/W by `PleoBooking` only — one row per Pleo entry posted or possibly posted and not yet in the 57200003 ledger; inserted before the post (the primary key is the claim), deleted when the ledger shows it, Holded refused it, or `ClearHolds` |
| PaymentDetails (`finance_payment_details`) | R/W — one row per member, keyed by `UserId` (bare id, no FK). A null `Iban` records a removal. |

Implements the internal `IPaymentDetailsManagement` (`GetIbanAsync` / `SetIbanAsync`, reached by Expenses
through `Service`'s `IFinanceService` / `IFinanceWrite`, and `RevealIbanAsync` for
`PaymentDetailsAdminController`); `IUserDataContributor` (slice `PaymentDetails`, masked IBAN; erasure
deletes the row) and `IUserMerge` (the survivor's IBAN wins, else the source's row moves).
Cross-section calls: `IUserService` (merge-chain resolution and the user-exists check) and
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


