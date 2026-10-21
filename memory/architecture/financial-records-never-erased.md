---
name: Financial records are never erased for GDPR
description: GDPR erasure never deletes or anonymizes financial records (payouts, invoices, ledger lines, bank movements); retain them as fiscal records and only sever the person link.
---

GDPR erasure never deletes or anonymizes financial records (payouts, invoices, ledger lines, bank movements); they are retained as fiscal records under Spanish commercial and tax law, and only the link to the person is severed where the schema allows.

**Why:** Peter 2026-10-21: "we don't retire financial stuff for gdpr reasons ever. don't even ask again." The books and their supporting documents must be kept 6 years (Código de Comercio Art. 30) and 4 years for tax purposes (Ley 58/2003 Art. 66); a financial record stripped of its counterparty is no longer evidence. GDPR Art. 17(3)(b).

**How to apply:** When a section exports financial data, its `ErasureDeclaration` entry states the retention basis (copy Finance's `PayoutRetention` wording) rather than `null`; `EraseForUserAsync` drops only the member binding (e.g. the creditor binding). Never raise erasing or anonymizing such rows as an option or a question.

**Related:** [[gdpr-is-a-feature-not-a-lens]]
