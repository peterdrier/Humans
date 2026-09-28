# Backdoor finance read API

## Business Context

Holded bookkeeping cleanup keeps turning up breaks that start in Humans: an expense doc posted to the wrong creditor account, a contact PUT that minted a new `400000xx`, a double-counted payable, SEPA transfers generated but never booked. The agent doing the books reads Holded directly but could not see Humans' side (which reports were pushed where, who is bound to which account, which transfers are booked) without screenshots of `/Finance/*`. `/api/backdoor/finance` exposes that side, read-only, to an API key. peterdrier/Humans#1838.

## User Stories

**As the owner of a Backdoor key with FinanceAdmin or Admin,** I can read expense reports with their Holded push state, creditor accounts with all their bindings, a creditor's ledger, the category map, SEPA transfers and the purchase-doc sync state, so I can reconcile Holded against Humans.

- All eight routes are `GET` and return JSON; nothing writes to the DB or Holded.
- No or unknown `X-Api-Key` → 401 on every route.
- An account with two bindings lists both; bindings without a `400000xx` appear in `unresolved`. Nothing picks a winner.
- The attachment route returns the stored bytes with their content type and file name.
- No response contains an unmasked IBAN — not in any JSON string, not in an attachment's download filename. The attachment bytes themselves pass through untouched.

**As a key owner without FinanceAdmin/Admin,** I see exactly what my `/Expenses` review queue would show me, and no more.

- 403 on the five finance-wide routes (creditor accounts, ledger, category map, SEPA transfers, Holded sync).
- `expense-reports` lists only my review queue; detail and attachment of any other report → 403.
- On a report I may view, payee name, masked IBAN and Holded timeline are shown only if I am its submitter (payment half) or a finance admin (push half). Holded contact, supplier-account and doc ids — report-level and per-line — are finance-admin only, the same split `/Expenses` shows in the browser.

## Data Model

No new tables. Backdoor reads Expenses through `IExpenseReportServiceRead` and Finance through `IHoldedFinanceServiceRead`; the category-map row and `SepaPayoutTransferRow` became public DTOs in `Humans.Finance.Contracts` for this.

## Related

- Routes and gating: [`Backdoor.md`](../Backdoor.md), [`authorization.md`](../authorization.md).
- Finance contract methods: [`Finance.md`](../../../Humans.Finance/Docs/Finance.md).
- Out of scope: writes (rebind, re-queue, book stay in `/Finance`), Pleo.
