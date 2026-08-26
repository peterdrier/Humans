---
name: iban-mask-in-logs
description: All IBAN output to logs / audit / errors goes through IbanFormatter.Mask
type: code
---

All IBAN output to logs / audit / errors goes through `IbanFormatter.Mask`.

**Why:** Spanish data protection + GDPR; raw IBAN is personal financial data.

**How to apply:** When you touch any log statement that references `Profile.Iban` or `ExpenseReport.PayeeIban`, wrap the value in `IbanFormatter.Mask(...)`. The only legitimate places raw IBAN appears are inside the `<IBAN>` element of the SEPA pain.001 XML, in the body of an outgoing Holded API request, and in an audit entry whose subject owns the IBAN — see [`audit-pii-subject-allowed`](audit-pii-subject-allowed.md).

**Surface area:** `src/Sections/Humans.Expenses/Services/`, `src/Sections/Humans.Expenses/Jobs/HoldedExpenseOutboxJob.cs`, `src/Sections/Humans.Expenses/Controllers/ExpensesController.cs`, any new admin route that touches IBAN.
