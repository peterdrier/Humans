# SEPA payout of creditor balances

Feature spec for nobodies-collective/Humans#1134 and #1141. Invariants live in
[`Finance.md`](../Finance.md); this is the how.

## What it does

`/Finance/Creditors` lets a finance admin tick member creditor accounts, adjust the amount, and
download a Norma 34-14 / **pain.001.001.09** SEPA Credit Transfer file to upload to Banco Sabadell's
"Enviar ficheros". Payouts operate on **creditor balances**, never on expense reports — no report
status, no member flag and no `Paid` state exists or moves.

`/Finance/Sepa` is the other half: every generated file with its transfers, each paired with the
outgoing Sabadell line that paid it once that line is on the bank feed. Sabadell debits one line
per transfer but drops the remittance text, so the pairing is on **amount and date**; when the text
does name the creditor account, on that too. Booking settles the transfer **against that bank
line**. A text-matched line books unattended in the two-hourly sweep or from the row's **Book**
button; an amount-matched one waits for the file's **Book file** approval, offered only when every
unbooked transfer in the file has exactly one line, and booking them all in one go. Booking pays the member's open purchase documents and posts one journal entry for whatever
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
   transfer rows in one save; one `AuditAction.SepaPayoutTransfer` entry per transfer follows, then
   one `sepa_payout_generated` email per transfer to the bound member — amount and masked IBAN, in
   their preferred language, `MessageCategory.System` (peterdrier/Humans#1820). Both come after the
   save so a rolled-back file leaves neither a ghost audit row nor a promise of money in the outbox.
   Notification failures are logged and do not block download of the saved file; a failed email
   does not suppress later recipients. After the save, notifications finish independently of request cancellation.
7. The XML streams back as `<org-slug>-<yyyy-MM-dd-HHmm>-<first 8 hex of the file id>.xml`. The
   stamp is minute-resolution, so the id suffix is what keeps two batches in one minute apart — the
   filename is the treasurer's handle on a downloaded copy and is quoted in the audit line.

Validation or file-construction failure refuses the **whole** batch and persists nothing. Post-save notification failures leave the saved file available.

## Booking a transfer into Holded

`/Finance/Sepa` lists the files newest first with their transfers, each `Generated` or `Booked`. A
booking is triggered two ways — the sweep (below) and the page's own Book button — and both call the
same `Service.BookSepaTransferAsync(transferId, bankMovementId, actorUserId)`
(`actorUserId` null = the sweep, audited under its job name).

1. **Find the line.** `client.ListBankMovementsAsync` over the last 92 days (the 90-day feed window
   plus the 2-day generation slack a line may precede its file by) on `Sepa:TreasuryAccountId`; the posted `bankMovementId` must be in it or the booking refuses.
2. **Validate the pairing, server-side** — never trust the posted id: the line is outgoing and its
   amount matches the transfer exactly; it is `pending` in Holded; if its description names a
   creditor account (the `<account> - NCA - <name>` remittance text) it is this transfer's
   `SupplierAccountNum`; a textless line is not also some multi-transfer file's total; exactly one
   unbooked transfer (of that account, or of any account for a textless line) matches it on amount
   and date; no other pending line could have paid this transfer; no other row already carries this
   movement id.
3. **Live balance.** `client.ListAccountingAccountsAsync()`, the row for the creditor account,
   `owedNow = -Balance` — the account's live total, not a windowed reconstruction.
4. **What is already posted**, from the ledger entries on the creditor account tagged with this
   transfer's row id — this is what makes a retry resume instead of double-pay. The window runs
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
   (nobodies-collective/Humans#1185), together with the comma-joined payment ids the run posted
   (`HoldedPaymentRefs`, plus an `entry:<amount>` token when a journal entry was posted). Holded's
   payment `POST` answers only `{"status":"paid"}`; the id is the entry of the document's
   `payments_detail` that is new after the POST and matches amount, date and bank account, read
   before and after (`PayPurchaseDocumentAsync`). The line is then reconciled in Holded against
   **those payment ids** (`{document_id: <payment id>, document_type: "payment"}`), all of them in
   one call first; when Holded refuses a list longer than `ReconcileChunkSize` (50) with a 4xx, the
   same ids go again in chunks of 50 against the same line, and the stamp then waits for the line
   to read `reconciled` on the feed. A refused chunk leaves the row pending for the sweep. A line
   that reads `partial` after every id was sent and the list was complete is logged at Warning with
   the line id, the amount and the ids sent. Never against the purchase document
   ids: a `purchase` ref reads as "this line pays that document", and Holded mints a brand-new
   payment for the whole line amount on the first document named, leaving the payments just posted
   as unmatched legs (production, 2026-10-17: a 75.00 line, a phantom 75.00 payment, 150.00 of
   debits). A payment whose ref came back `unconfirmed:` is left out, never replaced by its
   document id; a journal entry is left out too, because Holded cannot link a bank line to a
   ledger entry (`dailyledger` / `entry` are accepted and silently do nothing). Whenever something
   was left out — an unconfirmed payment, a journal remainder, a resumed run — Holded may read the
   line `partial`, so the stamp waits for the line to read `reconciled` on the feed; when every
   euro was named, Holded accepting the call is the reconcile. `ReconciledAt` means Holded says
   `reconciled`; anything else leaves it null, reconcile-pending, and the booking still stands —
   and when a journal entry was posted, the result and the audit entry say the line is a human's to
   reconcile in Holded.
   If the save itself fails after Holded accepted the postings, the money that moved is audited
   (`PARTIAL`) and the row stays unbooked — the next attempt posts only what is still missing.
9. One `AuditAction.SepaPayoutTransferBooked` entry follows, naming every Holded id, whether the run
   resumed, and whether the reconcile landed.

Open means **approved** (`draft: false`) and `payments_pending > 0`. A draft books nothing to the
ledger, so paying one would post against a document that does not exist for accounting.

### Known limits

- **A payout that needed a journal entry stays reconcile-pending until a human reconciles it.**
  The journal-entry remainder (the member's balance exceeds their open documents, e.g. Peter's loan,
  David's incoming transfer) can never be linked to a bank line by API; Holded only links treasury
  objects. The payments are linked and the entry's share of the line is not; such a line ends
  `partial` (or `pending`, when the whole payout was an entry) and is closed by hand in Holded. The
  row says so — "<amount> of this line was posted as a journal entry and needs a manual match in
  Holded", the amount read off the `entry:` token in `HoldedPaymentRefs` — instead of the generic
  reconcile-pending text, and the sweep's re-issue cannot help.
- **A retry after `PARTIAL` or `SHORT` is refused.** Holded discards the description on a purchase
  payment, so the `SEPA payout E<id>` tag never reaches the ledger for a payment and the step-4 sum
  reads 0 for it — a retry would post the money again as a journal entry. A run that posted
  payments and then stopped writes their ids to `HoldedPaymentRefs` with `BookedAt` still null
  (`PostedButNotBooked`); the page shows the reason in place of the button and `Book` refuses
  naming the ids. The transfer is settled in Holded by hand. A journal entry keeps its tag, so a
  run that posted *only* an entry still resumes from the tag sum.
- **A transfer older than the feed window can only be settled by hand.** Matching ignores rows
  generated more than 90 days ago — otherwise one stale row makes every later transfer of the same
  account and amount permanently ambiguous, with no way to clear it. Such a row renders with
  "generated more than 90 days ago — … settle it in Holded by hand" in place of a button, and stays
  `Generated` in Humans for the record.
- **`HoldedPaymentRefs` is back in use**, unbounded (`text`; the old 512 cap overflowed at about
  20 documents): the comma-joined payment ids a booking posted, in posting order, `unconfirmed:`
  sentinels included, plus `entry:<amount>` when a journal entry was posted. It is what the sweep
  re-issues the reconcile from (payment ids only; the sentinels and the entry token are never sent).
- **Outgoing lines are assumed to be negative** on the bank feed. If that is ever wrong, no line
  matches and every row waits — visible as rows that never leave "waiting for the Sabadell line".

### Refusals

| Condition | Shown as |
|-----------|----------|
| `Sepa:*` identity, `Sepa:TreasuryAccountId` or `Sepa:TreasuryLedgerAccount` unset | one banner for the whole screen; no buttons |
| Already booked | the row renders as `Booked`; a re-POST pays nothing and says so |
| No bank line yet | no button; "waiting for the Sabadell line" |
| Ambiguous match (two unbooked transfers generated inside the feed window fit one line — of its account, or of any account when its text names none) | the line renders in the "needs a human" panel; both are settled in Holded by hand |
| A textless line whose amount is also a multi-transfer file's total | neither the transfer nor the file takes it; the panel says so |
| A file with an unbooked transfer that has no line | no Book file button; the card says how many of its transfers are matched |
| A textless line dated outside the tolerance of every file of its amount | the panel; widen the tolerance on the page if it is that file's |
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
reaches the audit entry and, for a payment, `HoldedPaymentRefs`; it is never sent to Holded as a
reconcile ref.

## The sweep

`SepaBankBookingJob` (Hangfire id `sepa-bank-booking`, `17 */2 * * *` — every 2 hours) lists
unbooked transfers, reads the bank feed once over a window covering every candidate file, matches
lines **whose text names a creditor account** to transfers with the same rules as the button, and
books every unique match. A textless line is never booked by the sweep: Sabadell's lines carry no
text, so until that matching has earned trust a human approves each file on `/Finance/Sepa`. A line matching no transfer, or matching more than one, is skipped and surfaced on
`/Finance/Sepa`'s "bank lines needing a human" panel. The sweep also **re-issues the reconcile** for
every row still `ReconcilePending`: rows sharing one bank line (a Processed file) are re-issued
together, against the union of their recorded payment ids, when the line still reads `pending` in
Holded, through the same one-call-then-chunks path as the booking; the row is stamped when the line
then reads `reconciled`. A line Holded already reads
`reconciled` (a human in the GUI) is stamped as is. A line that reads `partial` is not re-issued —
its payments are linked and what remains (a journal entry) is a human's — and a row with no payment
id (booked before the ids were kept, or paid by an entry alone) only has its line's status read.
A file a Process run got part-way through is not re-issued either: linking the booked part would
turn the line `partial`, and the Process retry that finishes the file refuses a touched line.
Rows booked while the token could not write to the bank feed are what the re-issue exists for.
Every reconcile-pending row's line lies inside the window read for it: the read's lower
bound drops to the oldest pending row's first payable day with no feed floor, so a pending row
never ages out of its own re-check. The floor (90 days plus the 2-day generation slack) still bounds
*matching*, which takes no row generated outside the feed window anyway. A booking the sweep refuses
is logged at Warning. One bad line or one Holded exception never aborts the rest of the run.

## One line per transfer, no text — Book file

Sabadell's live behaviour: one outgoing line per transfer, dated the execution day, with no
remittance text echoed. `GetSepaPayoutsAsync` pairs such a line with the one unbooked transfer of
exactly its amount whose file date is within the **tolerance** of the line's date, inside the feed
window. The tolerance is days either side of the file's generation date, default
`IHoldedFinanceAdminService.DefaultSepaMatchToleranceDays` (2), editable at the top of
`/Finance/Sepa` (`?days=N`, clamped to 0..90) for a bank that executed a file late; the value posts
back with every booking from that view, so the booking re-validates under the window the page
paired with. A line whose text names the account is bound only from below (no earlier than the
tolerance before the file), as before. Nothing else is inferred:

- two unbooked transfers of that amount, in any files, make the line ambiguous — the panel names
  the count and a human books it in Holded;
- two pending lines of that amount that could each have paid one transfer leave it unpaired, both in
  the panel;
- a line whose amount is also a multi-transfer file's total is left to a human, since it could be
  the file's single debit instead.

Ambiguity is judged over **every** unbooked transfer in the window, bookable or not — the same set
the booking's own `PairingRefusal` uses — so a line the page pairs is one the booking accepts; a
line whose only match cannot be booked goes to the panel with that row's reason.

A paired row shows the line (date, amount, id, text) with a "matched on amount and date only"
badge and **no button of its own**. The file's card offers **Book file**
(`POST /Finance/Sepa/BookMatches` → `BookSepaFileMatchesAsync`) only when every unbooked transfer
in the file is paired; otherwise it says how many are. Book file re-runs the pairing, refuses the
whole file if any transfer is unmatched, and then books each row through the single-transfer path,
which re-validates its own line. A refusal part-way stops the run with the booked rows booked;
approving the file again books the rest. A file whose rows are all booked shows **Done**.

## One bank line for a whole file — Process

The file asks for one debit per transfer (`BtchBookg` false, below), but a bank may still debit a
multi-transfer file as a single line — the first Sabadell test showed one €100 line for a 10/20/30/40
file, with no creditor account in its text. The per-transfer matcher cannot pair that line with
anything, so it is matched to the **file** instead, and only a human books it.

`GetSepaPayoutsAsync` pairs an outgoing line with a file when (`BatchLineRefusal`):

- the file holds at least two transfers, and the line's amount is **exactly** their total;
- the line is `pending` in Holded, dated no earlier than the file's first payable day, and the file
  is inside the feed window;
- every transfer in the file is unbooked, or booked against **this** line (a Process that stopped
  part-way); no transfer of another file carries the line;
- the line's text does not name one transfer's account and amount — that is the transfer's own line;
- it is the only file that line fits, and the only line that fits that file.

A paired line renders on the file's card with its date, amount, id and text, and a badge saying
whether the text quotes the file's id (`MsgId`/`PmtInfId`) or it matched on total and date only. The
file's rows say "paid by the file's one bank line". A transfer in the file that cannot be booked
(unbound, rebound) is named instead of the button. An ambiguous line stays in the "needs a human"
panel with the reason.

**Process** (`POST /Finance/Sepa/BookFile` → `BookSepaFileAsync`, behind the same booking gate):

1. Re-reads the feed and re-runs the pairing, and the two ambiguity checks — the posted ids are never
   trusted.
2. Checks every unbooked transfer's binding **before** posting anything.
3. Books each unbooked transfer as a single booking would (live balance, tag resume, FIFO documents,
   journal remainder, dated the line, persisted before anything else), skipping only the
   per-transfer pairing and the reconcile. Each is audited as one of N transfers on that line.
4. Reconciles the line **once**, against every document and entry the run posted, and stamps
   `ReconciledAt` on every row of the file — audited `RECONCILED` per transfer. A run that resumed a
   part-processed file, or whose entry ref is unconfirmed, cannot name all of the line's postings,
   so it stamps only if Holded then reads the line `reconciled`; otherwise the rows stay
   reconcile-pending and the sweep's re-check picks them up.

A refusal part-way (Holded refuses a posting, a balance falls short) stops the run: transfers already
booked stay booked against the line, the failing one is audited as usual, and pressing Process again
books only the rest. The sweep never books a line for a whole file.

## The file

Root `Document` / `CstmrCdtTrfInitn` in `urn:iso:std:iso:20022:tech:xsd:pain.001.001.09`, UTF-8,
one `PmtInf` per recipient, each holding exactly one `CdtTrfTxInf`. Sabadell ignores `BtchBookg`
`false` inside a block and booked a three-transfer batch as one bank line, which breaks the
per-payment matching; banks that ignore the flag still book one debit per `PmtInf`.

- `GrpHdr`: `MsgId`, `CreDtTm`, `NbOfTxs`, `CtrlSum`, `InitgPty` (name + presenter id).
- `PmtInf` (one per transfer): `PmtInfId`, `PmtMtd` `TRF`, `BtchBookg` `false` (one debit per
  transfer — Norma 34-14's "indicador de apunte en cuenta" — so each bank line names its own creditor
  account), `NbOfTxs` `1`, `CtrlSum` (that transfer's amount), `SvcLvl/Cd` `SEPA`, `ReqdExctnDt`
  (generation date, Europe/Madrid), `Dbtr/Nm`, `DbtrAcct` IBAN, `DbtrAgt` (BIC when configured) —
  the debtor side repeats identically in every block.
- `CdtTrfTxInf`: `InstrId`, `EndToEndId`, `InstdAmt Ccy="EUR"`, `Cdtr/Nm`, `CdtrAcct` IBAN, one
  `RmtInf/Ustrd` — `"<account> - NCA - <creditor name>"`, carrying **this transfer's own** creditor
  account number and the payee's name (`NCA` is the fixed org tag between them) so a bank line ties
  back to an account and a person without opening the file.

`GrpHdr` `NbOfTxs` and `CtrlSum` stay file-level totals, computed off the blocks immediately before
serialization, so the header can never describe a different set than the one being sent.

**Deliberately absent**: postal addresses, `CdtrAgt`, `ChrgBr`, and any category-purpose code —
`SALA` above all, which would route a reimbursement as payroll.

### Identifiers

| Element | Source | Length |
|---------|--------|--------|
| `MsgId` | `"M"` + `SepaPayoutFile.Id` (`N` format) | 33 |
| `PmtInfId`, `InstrId`, `EndToEndId` | `"<account>-NCA-<NAME>"` — uppercase, SEPA subset, name truncated to fit | ≤ 35 |

The three transfer-level ids carry the same string so whichever one Sabadell echoes on the debtor
statement names the creditor account. The account number makes it unique per file (the same account
twice is refused upstream and by the builder). Holded postings are tagged with the persisted
transfer row id, which never changes.

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
| IBAN absent, containing non-ASCII account characters or non-digit check characters, or failing its checksum | service / builder |
| Over-long `MsgId`, or two transfers on the same creditor account | builder |
| Generated XML fails the XSD | builder |

## IBAN handling

The unmasked IBAN is stored only as sent: the generated XML and `sepa_payout_transfers.Iban`.
Logs, audit descriptions, the SEPA page and the cross-section `HoldedCreditorAccountRow` carry
`IbanFormatter.Mask(...)` output only; builder error messages mask the IBAN they name. An admin
screen (`/Finance/Creditors/{accountNum}`) and the member's own view may show it in full.

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
dated the bank line, the payment-id reconcile and when it stamps, the refused retry after a
PARTIAL or SHORT run, the sweep's re-issue, and its own booking pass — plus the
ways a booking could over-post or over-claim: a second booking racing the first, a resume from a
click-dated legacy posting, a resume that cannot close the gap, the tagged credit leg on the bank
account, a failed row save, and what `/Finance/Sepa` shows (the candidate line, the "needs a human"
reasons, the unreadable feed, and a stale row that no longer blocks a newer one) — plus the
one-line-for-a-file Process: the file card's line and reference badge, two files of one total, an
unprocessable file, every transfer booked and the line reconciled once, a wrong total, a bad binding
refused before any posting, a resumed file, and the sweep leaving the line alone.
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
