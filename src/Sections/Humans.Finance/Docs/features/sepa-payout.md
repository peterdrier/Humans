# SEPA payout of creditor balances

Feature spec for nobodies-collective/Humans#1134. Invariants live in
[`Finance.md`](../Finance.md); this is the how.

## What it does

`/Finance/Creditors` lets a finance admin tick member creditor accounts, adjust the amount, and
download a Norma 34-14 / **pain.001.001.09** SEPA Credit Transfer file to upload to Banco Sabadell's
"Enviar ficheros". Payouts operate on **creditor balances**, never on expense reports — no report
status, no member flag and no `Paid` state exists or moves. Settlement closes the ordinary way: the
treasurer books the payment in Holded and the next ledger sync zeroes the balance.

A partial payout is legitimate. The remainder stays on the balance and stays visible as owed.

## Configuration

| Key | Required | Meaning |
|-----|----------|---------|
| `Sepa:CreditorName` | yes | The organisation's legal name → `Dbtr/Nm` and `InitgPty/Nm` |
| `Sepa:CreditorIban` | yes | The account the money leaves → `DbtrAcct/Id/IBAN`. The flat `SEPA_CREDITOR_IBAN` env var overrides it, for deployments that cannot use dotted keys. |
| `Sepa:CreditorIdentifier` | yes | Presenter id (NIF + 3-char suffix) → `InitgPty/Id/OrgId/Othr/Id` |
| `Sepa:CreditorBic` | no | → `DbtrAgt/FinInstnId/BICFI`; omitted entirely when unset |
| `Sepa:MaxPayoutPerTransfer` | no | Hard per-transfer ceiling, default **50** |

The names are the pre-existing `Sepa:*` keys. "Creditor" is their historical spelling; in a payout
the organisation is the *debtor*, and that is where the values land. With any required key unset the
page says payout is unavailable and names the missing keys — nothing is ever inferred.

## Flow

1. `FinanceController.Creditors` renders each row's payability. Payable = **exactly one binding**,
   **positive balance from the member's side**, **an IBAN on the Holded contact**. Anything else
   shows the reason (`unbound`, `collision`, `nothing owed`, `no IBAN in Holded`) in place of a box.
2. The checkboxes and amount boxes belong to a standalone `#sepaForm` via the HTML5 `form`
   attribute — each row already carries an Unbind form and forms cannot nest.
3. `POST /Finance/Sepa/Generate` (`FinanceAdminOrAdmin`, antiforgery) parses the amounts
   **invariantly** rather than through model binding, which would use the request culture and read
   `12.34` as `1234` under a comma-decimal locale.
4. `Service.GenerateSepaPayoutAsync` re-derives payability server-side, checks each amount against
   the balance, resolves the payee's name and unmasked IBAN from the cached Holded contact list
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

## The file

Root `Document` / `CstmrCdtTrfInitn` in `urn:iso:std:iso:20022:tech:xsd:pain.001.001.09`, UTF-8,
one `PmtInf`, one `CdtTrfTxInf` per recipient.

- `GrpHdr`: `MsgId`, `CreDtTm`, `NbOfTxs`, `CtrlSum`, `InitgPty` (name + presenter id).
- `PmtInf`: `PmtInfId`, `PmtMtd` `TRF`, `NbOfTxs`, `CtrlSum`, `SvcLvl/Cd` `SEPA`, `ReqdExctnDt`
  (generation date, Europe/Madrid), `Dbtr/Nm`, `DbtrAcct` IBAN, `DbtrAgt` (BIC when configured).
- `CdtTrfTxInf`: `EndToEndId`, `InstdAmt Ccy="EUR"`, `Cdtr/Nm`, `CdtrAcct` IBAN, one
  `RmtInf/Ustrd` ("Nobodies expense reimbursement").

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

## Refusals

Server-side, all-or-nothing:

| Condition | Where |
|-----------|-------|
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
**masked** IBAN. Article 17 retains the whole record — a payout file is the credit-transfer order
the bank was given, and Spanish law requires the books and their supporting documents be kept
(Código de Comercio Art. 30, Ley 58/2003 Art. 66; GDPR Art. 17(3)(b)). The basis is stated in
`Service.PayoutRetention` and enforced by `GdprErasureCoverageTests`.

## Tests

`tests/Humans.Finance.Tests/SepaPaymentFileBuilderTests.cs` covers the builder end to end. Because
`Build` validates every file it returns against the embedded XSD, any test that receives a string
has already proved schema conformance — there is no separate validation test.
`ServiceTests.cs` covers the service-side gates: unavailable config, over-balance, over-cap, partial
amounts, missing IBAN, the masked-only audit entry, two contacts on one account paying the bound one,
and the masked Article 15 slice.

## Not done here

The live "Enviar ficheros" upload against a real Sabadell session is a manual acceptance step.
