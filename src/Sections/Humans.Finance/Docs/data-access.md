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
`SepaPayoutFiles`, `SepaPayoutTransfers`.

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

### HoldedFinanceService (Scoped)

Repository: `IHoldedRepository`.

| Table | R/W |
|-------|-----|
| HoldedCategoryMap | R/W |
| HoldedExpenseDocs | R/W |
| HoldedCreditorContacts | R/W (creditor-contact bindings per user) |
| HoldedDocSyncStates | R/W |
| SepaPayoutFiles | R/W (append-only writes; read joined for the Article 15 export and for `/Finance/Sepa`) |
| SepaPayoutTransfers | R/W (appended at generation; the booking columns are the only update — `SaveSepaTransferBookingAsync`. Read per user for the Article 15 export and flattened with the file for `/Finance/Sepa`) |

Cross-section calls via `IBudgetServiceRead` (migrated to the read-split
surface — `budget` in the ctor), `IHoldedService` (the Holded section's
ledger-mirror read surface — `holded` in the ctor; ledger-line /
account-balance reads for creditor status, ledger, and account listing),
`IHoldedClient` (Holded section leaf — purchase-document / contact / expense-account
API calls, plus `PayPurchaseDocumentAsync` for SEPA booking) and `IAuditLogService`
(one entry per SEPA credit transfer generated, and one per transfer booked).
Implements `IHoldedFinanceService`, `IHoldedFinanceAdminService`
and `IUserDataContributor`.
Injects `IMemoryCache` for the 2-minute Holded contact-list entry
(`CacheKeys.HoldedContacts`) and `IOptions<SepaOptions>` for the organisation's
SEPA identity.

`IHoldedFinanceAdminService` (`Services/IHoldedFinanceAdminService.cs`) is
**internal** — the `/Finance/Holded` connector index is this section's own
screen, so its read model is not cross-section surface; the same shape as the
Holded section's `IHoldedAdminService`. Its one method,
`GetConnectorOverviewAsync`, reads `HoldedDocSyncStates`,
`HoldedCategoryMap`, `HoldedExpenseDocs` (via `GetAllDocsAsync` — the
unmatched and matched-for-year reads are each a filtered slice and neither
composes into "all docs") and `HoldedCreditorContacts`, plus
`IBudgetServiceRead` for category names. **No `IHoldedClient` call** — the
index must not inherit the connector's 30 s timeout
(nobodies-collective/Humans#976, #1000). Its other methods,
`GetSepaPayoutSettings`, `GenerateSepaPayoutAsync`, `GetSepaPayoutsAsync` and
`BookSepaTransferAsync`, serve `/Finance/Creditors`' payout column,
`POST /Finance/Sepa/Generate`, `GET /Finance/Sepa` and
`POST /Finance/Sepa/Book` — also this section's own screens, so also not
cross-section surface. `BookSepaTransferAsync` is marked `[ExternalWrite]` and
takes no `CancellationToken`: it posts payments to Holded and has to finish once
it has started
([`cancellation-token-propagation`](../../../../memory/architecture/cancellation-token-propagation.md)).

### SepaPaymentFileBuilder / SepaText / SepaSchema

Pure static helpers — pain.001.001.09 assembly, SEPA character folding and
XSD validation against the embedded official schema. No DI dependencies, no
DB access, no IO.

### HoldedMatcher

Stateless matcher — pairs Holded docs against budget categories. No DI
dependencies beyond pure data shaping, no DB access.

---


