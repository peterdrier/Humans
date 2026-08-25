# SEPA payout of creditor balances

Feature spec for nobodies-collective/Humans#1134 and #1141. Invariants live in
[`Finance.md`](../Finance.md); this is the how.

## What it does

`/Finance/Creditors` lets a finance admin tick member creditor accounts, adjust the amount, and
download a Norma 34-14 / **pain.001.001.09** SEPA Credit Transfer file to upload to Banco Sabadell's
"Enviar ficheros". Payouts operate on **creditor balances**, never on expense reports — no report
status, no member flag and no `Paid` state exists or moves.

`/Finance/Sepa` is the other half: every generated file with its transfers, and per transfer a
**Book** button that posts the payment into Holded against the member's open purchase documents. The
next ledger sync then zeroes the creditor balance. Booking is the only thing that moves a transfer
out of `Generated`.

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
   **keyed on the binding's `HoldedContactId`**, and mints one `SepaPayoutTransfer` per row. Two
   Holded contacts can share one 400000xx; keying by account number instead would pay whichever of
   them Holded happened to list first, which is not necessarily the bound member.
5. `SepaPaymentFileBuilder.Build` enforces the file-level rules, serializes, and validates against
   the embedded official XSD. It is pure — no IO, no clock, no configuration.
6. The file, its SHA-256 checksum, the timestamp and the generating admin are persisted with the
   transfer rows in one save; one `AuditAction.SepaPayoutTransfer` entry per transfer follows.
7. The XML streams back as `<org-slug>-<yyyy-MM-dd-HHmm>-<first 8 hex of the file id>.xml`. The
   stamp is minute-resolution, so the id suffix is what keeps two batches in one minute apart — the
   filename is the treasurer's handle on a downloaded copy and is quoted in the audit line.

Any failure at any step refuses the **whole** batch with a message and persists nothing.

## Booking a transfer into Holded

`/Finance/Sepa` lists the files newest first with their transfers, each `Generated` or `Booked`.

1. `Service.GetSepaPayoutsAsync` reads the transfer rows, the creditor bindings and — once for the
   whole screen — Holded's purchase documents, and fills in per row why it cannot be booked. A
   vendor failure there costs the pre-check only: the row still offers the button and the booking
   re-derives coverage itself.
2. `POST /Finance/Sepa/Book` (`FinanceAdminOrAdmin`, antiforgery) calls
   `Service.BookSepaTransferAsync`, which takes **no `CancellationToken`** — once a payment has been
   posted to Holded the rest of the allocation has to finish whether or not the admin is still
   watching ([`cancellation-token-propagation`](../../../../memory/architecture/cancellation-token-propagation.md)).
3. The service re-checks every gate, then allocates the transfer amount across the member's open
   purchase documents **oldest first**, paying `min(remaining, payments_pending)` per document via
   `POST /api/v2/purchases/{id}/payments` on the configured treasury account. The payment date is
   the booking date (Holded's bank-feed reconciliation matches on amount + date) and the description
   is `SEPA payout E<transfer id>`, the file's own `EndToEndId`.
4. `BookedAt`, the acting admin and the comma-joined Holded payment ids land on the transfer row,
   then one `AuditAction.SepaPayoutTransferBooked` entry follows.

Open means **approved** (`draft: false`) and `payments_pending > 0`. A draft books nothing to the
ledger, so paying one would post against a document that does not exist for accounting.

### Refusals

| Condition | Shown as |
|-----------|----------|
| `Sepa:*` identity or `Sepa:TreasuryAccountId` unset | one banner for the whole screen; no buttons |
| Already booked | the row renders as `Booked`; a re-POST pays nothing and says so |
| Partially booked (refs, no `BookedAt`) | the reason and the accepted ids, in place of the button; never re-bookable |
| Member has no `HoldedCreditorContact` binding | the reason, in place of the button |
| The binding's `SupplierAccountNum` no longer matches the transfer's | the reason, in place of the button |
| Open documents cover less than the transfer amount | the reason, with the shortfall |
| Holded unreadable at booking time | an error; nothing is posted |

### When Holded accepts one payment and refuses the next

The payment ids already created are persisted and `BookedAt` stays **null** — nothing claims the
transfer settled — and one `AuditAction.SepaPayoutTransferBooked` entry follows, labelled `PARTIAL`
with the accepted ids.

That state is **terminal**: `HoldedPaymentRefs` with a null `BookedAt` refuses re-booking on its own,
before coverage is even looked at. Coverage cannot stand in for it — a member owed more than the
per-transfer cap still has enough pending after a partial failure, so a retry would pass the coverage
check and post the full amount a second time. The screen shows the row as partially booked with the
ids and no button; the remainder is finished in Holded by hand, and there is no affordance to mark a
transfer booked without paying through it.

A payment Holded **accepted** but gave no readable id for is not a failure at all: the client returns
`"unconfirmed:{documentId}"`, the allocation continues, and the transfer books normally. The sentinel
lands in `HoldedPaymentRefs` and in the audit entry, naming the document the treasurer has to eyeball
in Holded. Throwing there would have lost a real payment from the record and left the transfer
retryable — so reaching the catch on the *first* payment now genuinely means Holded refused it.

## The file

Root `Document` / `CstmrCdtTrfInitn` in `urn:iso:std:iso:20022:tech:xsd:pain.001.001.09`, UTF-8,
one `PmtInf`, one `CdtTrfTxInf` per recipient.

- `GrpHdr`: `MsgId`, `CreDtTm`, `NbOfTxs`, `CtrlSum`, `InitgPty` (name + presenter id).
- `PmtInf`: `PmtInfId`, `PmtMtd` `TRF`, `NbOfTxs`, `CtrlSum`, `SvcLvl/Cd` `SEPA`, `ReqdExctnDt`
  (generation date, Europe/Madrid), `Dbtr/Nm`, `DbtrAcct` IBAN, `DbtrAgt` (BIC when configured).
- `CdtTrfTxInf`: `EndToEndId`, `InstdAmt Ccy="EUR"`, `Cdtr/Nm`, `CdtrAcct` IBAN, one
  `RmtInf/Ustrd` — `"<400000xx> Nobodies expense reimbursement"`, prefixed with **this transfer's**
  creditor account number so a bank line ties back to an account without opening the file.

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

The unmasked IBAN exists in exactly two places: the generated XML and `sepa_payout_transfers.Iban`.
Everything else — logs, audit descriptions, the page, and even the cross-section
`HoldedCreditorAccountRow` — carries `IbanFormatter.Mask(...)` output only. Builder error messages
mask the IBAN they name.

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
`ServiceTests.cs` covers the service-side gates: unavailable config, over-balance, over-cap (the
posted cap governs, not the config default), partial amounts, missing IBAN, the masked-only audit
entry, two contacts on one account paying the bound one, and the masked Article 15 slice — plus the
booking gates (already booked, partially booked, unbound member, rebound member, unconfigured
treasury, thin coverage), the oldest-first allocation, the mid-allocation failure and its audit
entry, and what `/Finance/Sepa` renders in each case.
`FinanceControllerTests.cs` covers the posted-cap parsing: unparseable or non-positive refuses
before the service is called, a valid cap is parsed invariantly and passed through — and the SEPA
screen's file grouping. `RepositoryTests.cs` pins the booking stamp and the screen's flattened read;
`Humans.Holded.Tests` pins the payment POST's decimal-string amount, the omitted-`treasury_id`
shape, and the unreadable-response-is-permanent rule.

## Not done here

The live "Enviar ficheros" upload against a real Sabadell session is a manual acceptance step, and
so is verifying a booked payment against the real Holded account.
