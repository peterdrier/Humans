# SEPA payout of creditor balances

Feature spec for nobodies-collective/Humans#1134 and #1141. Invariants live in
[`Finance.md`](../Finance.md); this is the how.

## What it does

`/Finance/Creditors` lets a finance admin tick member creditor accounts, adjust the amount, and
download a Norma 34-14 / **pain.001.001.09** SEPA Credit Transfer file to upload to Banco Sabadell's
"Enviar ficheros". Payouts operate on **creditor balances**, never on expense reports — no report
status, no member flag and no `Paid` state exists or moves.

`/Finance/Sepa` is the other half: every generated file with its transfers, and per transfer either
a **Book** button — once the outgoing Sabadell line has appeared on the bank feed — or a wait state.
Booking no longer "settles it now": it settles **against that bank line**, and a recurring sweep
does the same thing unattended every two hours, so most transfers book without anyone touching the
button. Booking pays the member's open purchase documents and posts one journal entry for whatever
those do not cover. The next ledger sync then zeroes the creditor balance. Booking is the only thing
that moves a transfer out of `Generated`.

A partial payout is legitimate. The remainder stays on the balance and stays visible as owed.

## Configuration

| Key | Required | Meaning |
|-----|----------|---------|
| `Sepa:CreditorName` | yes | The organisation's legal name → `Dbtr/Nm` and `InitgPty/Nm` |
| `Sepa:CreditorIban` | yes | The account the money leaves → `DbtrAcct/Id/IBAN`. The flat `SEPA_CREDITOR_IBAN` env var overrides it, for deployments that cannot use dotted keys. |
| `Sepa:CreditorIdentifier` | yes | Presenter id (NIF + 3-char suffix) → `InitgPty/Id/OrgId/Othr/Id` |
| `Sepa:CreditorBic` | no | → `DbtrAgt/FinInstnId/BICFI`; omitted entirely when unset |
| `Sepa:MaxPayoutPerTransfer` | no | Per-transfer cap **prefill default**, **50**; the admin can raise or lower it per batch on the screen |
| `Sepa:TreasuryAccountId` | for booking | The Holded treasury account a booked payout is paid from → `treasury_id`. Unset, `/Finance/Sepa` says so and offers no Book buttons. Never inferred: Holded would otherwise fall back to whichever account it defaults to. |
| `Sepa:TreasuryLedgerAccount` | for booking | The ledger account number behind that treasury (Sabadell's `572xxxxx`) → the credit side of the journal entry that settles what open documents do not cover. Unset, `/Finance/Sepa` says so and offers no Book buttons. |

The names are the pre-existing `Sepa:*` keys. "Creditor" is their historical spelling; in a payout
the organisation is the *debtor*, and that is where the values land. With any required key unset the
page says payout is unavailable and names the missing keys — nothing is ever inferred.

The per-transfer cap itself is **not** config-only: `/Finance/Creditors` shows it as an editable
field next to the Generate button, prefilled from `Sepa:MaxPayoutPerTransfer`, and the value posted
with the batch is what `GenerateSepaPayoutAsync` enforces — changing the cap for a one-off batch no
longer needs a redeploy.

## Flow

1. `FinanceController.Creditors` renders each row's payability. Payable = **exactly one binding**,
   **positive balance from the member's side**, **an IBAN on the Holded contact**. Anything else
   shows the reason (`unbound`, `collision`, `nothing owed`, `no IBAN in Holded`) in place of a box.
   An **"Only show accounts we owe money to"** checkbox, **ticked by default**, hides every row whose
   balance is not positive so the screen opens on just the accounts a payout would touch. It is a
   client-side row filter over the already-rendered table — every row is present in the markup, and
   the selection still posts from `#sepaForm` where only payable rows carry a checkbox — so the
   filter changes what is shown, never what a Generate would include.
2. The checkboxes, amount boxes and the per-transfer cap field belong to a standalone `#sepaForm`
   via the HTML5 `form` attribute — each row already carries an Unbind form and forms cannot nest.
   The cap field is prefilled from `Sepa:MaxPayoutPerTransfer` but is editable per batch.
3. `POST /Finance/Sepa/Generate` (`FinanceAdminOrAdmin`, antiforgery) parses the amounts and the cap
   **invariantly** rather than through model binding, which would use the request culture and read
   `12.34` as `1234` under a comma-decimal locale. An unparseable or non-positive cap refuses the
   whole batch before any selection is looked at.
4. `Service.GenerateSepaPayoutAsync` takes the posted cap as a parameter (the config key is only the
   screen's prefill default), re-derives payability server-side, checks each amount against the
   balance, resolves the payee's name and unmasked IBAN from the cached Holded contact list
   **keyed on the binding's `HoldedContactId`**, and mints one `SepaPayoutTransfer` per row,
   stamping that same `HoldedContactId` onto it. Two Holded contacts can share one 400000xx;
   keying by account number instead would pay whichever of them Holded happened to list first,
   which is not necessarily the bound member — and the stamped id is what booking later checks the
   binding against, so a rebind to the sibling contact after generation cannot pay it instead
   (nobodies-collective/Humans#1146).
5. `SepaPaymentFileBuilder.Build` enforces the file-level rules, serializes, and validates against
   the embedded official XSD. It is pure — no IO, no clock, no configuration.
6. The file, its SHA-256 checksum, the timestamp and the generating admin are persisted with the
   transfer rows in one save; one `AuditAction.SepaPayoutTransfer` entry per transfer follows.
7. The XML streams back as `<org-slug>-<yyyy-MM-dd-HHmm>-<first 8 hex of the file id>.xml`. The
   stamp is minute-resolution, so the id suffix is what keeps two batches in one minute apart — the
   filename is the treasurer's handle on a downloaded copy and is quoted in the audit line.

Any failure at any step refuses the **whole** batch with a message and persists nothing.

## Booking a transfer into Holded

`/Finance/Sepa` lists the files newest first with their transfers, each `Generated` or `Booked`. A
booking is triggered two ways — the sweep (below) and the page's own Book button — and both call the
same `Service.BookSepaTransferAsync(transferId, bankMovementId, actorUserId)`
(`actorUserId` null = the sweep, audited under its job name).

1. **Find the line.** `client.ListBankMovementsAsync` over the last 92 days (the 90-day feed window
   plus the 2-day generation slack a line may precede its file by) on `Sepa:TreasuryAccountId`; the posted `bankMovementId` must be in it or the booking refuses.
2. **Validate the pairing, server-side** — never trust the posted id: the line is outgoing and its
   amount matches the transfer exactly; it is not already `reconciled`; its description names this
   transfer's `SupplierAccountNum` (the `<account> - NCA - <name>` remittance text); exactly one
   unbooked transfer matches it; no other row already carries this movement id.
3. **Live balance.** `client.ListAccountingAccountsAsync()`, the row for the creditor account,
   `owedNow = -Balance` — the account's live total, not a windowed reconstruction.
4. **What is already posted**, from the ledger entries on the creditor account tagged with this
   transfer's `EndToEndId` — this is what makes a retry resume instead of double-pay. The window runs
   from the day the file was generated (or the bank line, whichever is earlier) to today, with a
   week of slack either end: postings this flow makes are dated the bank line, but a pre-#1185 run
   dated them the **click**, which can be weeks off. The account number is filtered here as well as
   in the query — both legs of our journal entry carry the same tag, so an unfiltered read would net
   to zero and re-post everything.
5. **The arithmetic**: `toPost = min(transfer.Amount - posted, owedNow)`, rounded to cents. Fresh
   and not enough owed → refuse, post nothing. Already fully posted → post nothing, skip to
   reconcile. Otherwise post the gap.
6. **FIFO document payments**, oldest first, dated the **bank line's** date (not today) —
   `POST /api/v2/purchases/{id}/payments`, description `SEPA payout E<transfer id>`.
7. **The remainder as one journal entry**, same date and tag, debit the creditor account, credit
   `Sepa:TreasuryLedgerAccount`.
7b. **The gap has to be closed.** `owedNow` caps `toPost`, so a resume against an account that no
   longer owes the outstanding part can only post some of it. Then nothing is stamped: the shortfall
   is audited (`SHORT SEPA booking …`, naming what is posted and what the account owes), the admin is
   told, and the row stays unbooked and retryable. `Booked` never means less than the transfer's
   amount reached the ledger.
8. **Persist first, then reconcile.** `BookedAt`, the acting admin (or null for the sweep) and the
   bank movement id land on the row before anything is reconciled — the local save is the cheap
   write, and losing it after Holded already took the money was the original bug
   (nobodies-collective/Humans#1185). The line is then reconciled in Holded against the paid
   documents and the journal entry; a refused `dailyledger` type retries with the documents alone.
   That retry, like a reconcile whose entry ref came back `unconfirmed:` and so was never named,
   drops a remainder Holded may leave the line `partial` against — so either stamps only if the
   line then reads `reconciled` on the feed. `ReconciledAt` means Holded says `reconciled`;
   anything else leaves it null, reconcile-pending, and the booking still stands.
   If the save itself fails after Holded accepted the postings, the money that moved is audited
   (`PARTIAL`) and the row stays unbooked — the next attempt posts only what is still missing.
9. One `AuditAction.SepaPayoutTransferBooked` entry follows, naming every Holded id, whether the run
   resumed, and whether the reconcile landed.

Open means **approved** (`draft: false`) and `payments_pending > 0`. A draft books nothing to the
ledger, so paying one would post against a document that does not exist for accounting.

### Known limits

- **A resumed booking usually ends reconcile-pending.** The reconcile payload is built from the
  documents *this run* paid; documents an earlier run already settled come back with nothing pending
  and are skipped, and Holded's purchase list does not expose which payment settled them. So a
  booking that resumed after a crash normally leaves `ReconciledAt` null, and the line is ticked
  either by a human in the Holded GUI or by the sweep noticing Holded now reports it reconciled.
  Nothing is posted twice; only the reconcile is deferred.
- **A transfer older than the feed window can only be settled by hand.** Matching ignores rows
  generated more than 90 days ago — otherwise one stale row makes every later transfer of the same
  account and amount permanently ambiguous, with no way to clear it. Such a row renders with
  "generated more than 90 days ago — … settle it in Holded by hand" in place of a button, and stays
  `Generated` in Humans for the record.
- **`HoldedPaymentRefs` is retained unused.** The column that held the old payment refs stays on
  `sepa_payout_transfers` with nothing reading or writing it; this change's migration only *adds*
  `HoldedBankMovementId` and `ReconciledAt`. Dropping it is a follow-up PR of its own and needs
  Peter's approval ([`no-drops-until-prod-verified`](../../../../../memory/architecture/no-drops-until-prod-verified.md)).
- **Outgoing lines are assumed to be negative** on the bank feed, and a `dailyledger` reconcile
  target is unconfirmed (the ladder covers the second). If the first is ever wrong, no line matches
  and every row waits — visible as rows that never leave "waiting for the Sabadell line".

### Refusals

| Condition | Shown as |
|-----------|----------|
| `Sepa:*` identity, `Sepa:TreasuryAccountId` or `Sepa:TreasuryLedgerAccount` unset | one banner for the whole screen; no buttons |
| Already booked | the row renders as `Booked`; a re-POST pays nothing and says so |
| No bank line yet | no button; "waiting for the Sabadell line" |
| Ambiguous match (two unbooked transfers generated inside the feed window fit one line) | the line renders in the "needs a human" panel; both are settled in Holded by hand |
| The run could post only part of the transfer (the account owes less than the gap) | refused and audited `SHORT`; the row stays unbooked |
| The transfer's file is older than the 90-day feed window | the reason, in place of the button |
| The live balance owes less than the transfer | refused; nothing posted |
| The bank line is already reconciled or partly reconciled in Holded, or already booked to another transfer | refused; nothing posted |
| The bank line is dated before the file that asked for the transfer was generated (less two days' slack) | it does not match; the line renders in the "needs a human" panel |
| Two unreconciled lines could each have paid one transfer | refused; both render in the "needs a human" panel |
| Member has no `HoldedCreditorContact` binding | the reason, in place of the button |
| The binding's `SupplierAccountNum` no longer matches the transfer's | the reason, in place of the button |
| The binding's `HoldedContactId` no longer matches the transfer's (rebound to a sibling contact on the same account, nobodies-collective/Humans#1146) | the reason, in place of the button. Skipped for a transfer generated before this guard existed (`HoldedContactId` null) — account-only. |
| Holded unreadable at booking time | an error; nothing is posted |

### When Holded accepts one posting and refuses the next

The state is **no longer terminal**. On a Holded failure mid-allocation, the ids already accepted
are logged and audited (`SepaPayoutTransferBooked`, prefixed `PARTIAL`), but **nothing is written to
the transfer row** — the row stays unbooked and is retryable. The next attempt re-reads what is
already posted under the transfer's tag (step 4 above) and continues from there, so nothing is ever
posted twice.

A posting Holded **accepted** but gave no readable id for is not a failure at all: the client returns
`"unconfirmed:{documentId}"` (or `"unconfirmed:entry"`), and the allocation continues. The sentinel
now only reaches the audit entry — nothing is stored on the row for it.

## The sweep

`SepaBankBookingJob` (Hangfire id `sepa-bank-booking`, `17 */2 * * *` — every 2 hours) is the normal
trigger: it lists unbooked transfers, reads the bank feed once over a window covering every
candidate file, matches lines to transfers with the same rules as the button, and books every unique
match. A line matching no transfer, or matching more than one, is skipped and surfaced on
`/Finance/Sepa`'s "bank lines needing a human" panel. The sweep also retries the reconcile for every
row still `ReconcilePending`, by checking whether the stored bank line now reads `reconciled` in
Holded. Every reconcile-pending row's line lies inside the window read for it: the read's lower
bound drops to the oldest pending row's first payable day with no feed floor, so a pending row
never ages out of its own re-check. The floor (90 days plus the 2-day generation slack) still bounds
*matching*, which takes no row generated outside the feed window anyway. A booking the sweep refuses
is logged at Warning. One bad line or one Holded exception never aborts the rest of the run.

## The file

Root `Document` / `CstmrCdtTrfInitn` in `urn:iso:std:iso:20022:tech:xsd:pain.001.001.09`, UTF-8,
one `PmtInf`, one `CdtTrfTxInf` per recipient.

- `GrpHdr`: `MsgId`, `CreDtTm`, `NbOfTxs`, `CtrlSum`, `InitgPty` (name + presenter id).
- `PmtInf`: `PmtInfId`, `PmtMtd` `TRF`, `NbOfTxs`, `CtrlSum`, `SvcLvl/Cd` `SEPA`, `ReqdExctnDt`
  (generation date, Europe/Madrid), `Dbtr/Nm`, `DbtrAcct` IBAN, `DbtrAgt` (BIC when configured).
- `CdtTrfTxInf`: `EndToEndId`, `InstdAmt Ccy="EUR"`, `Cdtr/Nm`, `CdtrAcct` IBAN, one
  `RmtInf/Ustrd` — `"<account> - NCA - <creditor name>"`, carrying **this transfer's own** creditor
  account number and the payee's name (`NCA` is the fixed org tag between them) so a bank line ties
  back to an account and a person without opening the file.

Counts and control sums are computed off the transaction elements immediately before serialization,
so the header can never describe a different set than the one being sent.

**Deliberately absent**: postal addresses, `CdtrAgt`, `ChrgBr`, and any category-purpose code —
`SALA` above all, which would route a reimbursement as payroll.

### Identifiers

| Element | Source | Length |
|---------|--------|--------|
| `MsgId` | `"M"` + `SepaPayoutFile.Id` (`N` format) | 33 |
| `PmtInfId` | `"P"` + `SepaPayoutFile.Id` | 33 |
| `EndToEndId` | `"E"` + `SepaPayoutTransfer.Id` | 33 |

Row ids are minted before the file is built and never change, so the id the bank quotes always
resolves to one persisted transfer. Duplicates are refused.

### Character handling

`SepaText.Normalize` folds text into the restricted subset Sabadell accepts —
`a-z A-Z 0-9 / - ? : ( ) . , ' +` and space. Accents decompose (Ñ→N, Ç→C, Á→A); Ø, Æ, Œ, ß, Ł, Þ, Ð
map by hand; anything left over becomes a space, runs collapse. Names cap at 70, remittance at 140.
XML-reserved characters are escaped by the writer, not stripped.

## Generation refusals

Server-side, all-or-nothing:

| Condition | Where |
|-----------|-------|
| Posted cap unparseable or not positive | controller |
| Required config missing | service |
| Nothing selected, or one account selected twice | service |
| Account unbound, or bound to more than one member | service |
| Amount above the balance | service |
| Amount below €0.01, more than 2 decimals, or above the cap | builder |
| IBAN absent, or failing its check digits | service / builder |
| Duplicate or over-long `MsgId` / `PmtInfId` / `EndToEndId` | builder |
| Generated XML fails the XSD | builder |

## IBAN handling

The unmasked IBAN is stored only as sent: the generated XML and `sepa_payout_transfers.Iban`.
Logs, audit descriptions, the SEPA page and the cross-section `HoldedCreditorAccountRow` carry
`IbanFormatter.Mask(...)` output only; builder error messages mask the IBAN they name. An admin
screen (`/Finance/CreditorStatement`) and the member's own view may show it in full.

## GDPR

`SepaPayouts` is the member's Article 15 slice: every transfer paid to them, oldest first, with the
**masked** IBAN and when it was booked. The Holded payment ids and the booking admin are not in it —
they are internal accounting references, not the member's data. Article 17 retains the whole record — a payout file is the credit-transfer order
the bank was given, and Spanish law requires the books and their supporting documents be kept
(Código de Comercio Art. 30, Ley 58/2003 Art. 66; GDPR Art. 17(3)(b)). The basis is stated in
`Service.PayoutRetention` and enforced by `GdprErasureCoverageTests`.

## Tests

`tests/Humans.Finance.Tests/SepaPaymentFileBuilderTests.cs` covers the builder end to end. Because
`Build` validates every file it returns against the embedded XSD, any test that receives a string
has already proved schema conformance — there is no separate validation test.
`ServiceTests.cs` covers the file-generation service-side gates: unavailable config, over-balance,
over-cap (the posted cap governs, not the config default), partial amounts, missing IBAN, the
masked-only audit entry, two contacts on one account paying the bound one, and the masked Article 15
slice — plus the booking guards that do not depend on the bank line (already booked, unbound
member, rebound member, unconfigured treasury and ledger account) and what `/Finance/Sepa` renders
in each case. `SepaBankBookingTests.cs` covers the bank-line-driven flow: the matcher (amount,
account, ambiguity, already-booked, already-reconciled), the resume arithmetic, FIFO allocation
dated the bank line, the reconcile ladder and its fallback, and the sweep's own pass — plus the
ways a booking could over-post or over-claim: a second booking racing the first, a resume from a
click-dated legacy posting, a resume that cannot close the gap, the tagged credit leg on the bank
account, a failed row save, and what `/Finance/Sepa` shows (the candidate line, the "needs a human"
reasons, the unreadable feed, and a stale row that no longer blocks a newer one).
`FinanceControllerTests.cs` covers the posted-cap parsing: unparseable or non-positive refuses
before the service is called, a valid cap is parsed invariantly and passed through — the SEPA
screen's file grouping, the "needs a human" panel, and the bank-feed-unreadable banner.
`RepositoryTests.cs` pins the booking stamp (bank movement id, reconcile timestamp) and the screen's
flattened read; `Humans.Holded.Tests` pins the payment POST's decimal-string amount, the
omitted-`treasury_id` shape, the ledger-entry POST's two balanced lines, the unreadable-response-is-
permanent rule, and the bank-movements feed's parsing and reconcile call.

## Not done here

The live "Enviar ficheros" upload against a real Sabadell session is a manual acceptance step, and
so is verifying a booked payment against the real Holded account.
