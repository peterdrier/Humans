---
name: iban-mask-in-logs
description: All IBAN output to logs / audit / errors goes through IbanFormatter.Mask
type: code
---

All IBAN output to logs / audit / errors goes through `IbanFormatter.Mask`.

**Why:** Spanish data protection + GDPR; raw IBAN is personal financial data.

**How to apply:** When you touch any log statement that references `Profile.Iban` or `ExpenseReport.PayeeIban`, wrap the value in `IbanFormatter.Mask(...)`. The only legitimate places raw IBAN appears are inside the `<IBAN>` element of the SEPA pain.001 XML and in the body of an outgoing Holded API request.

**Surface area:** `src/Sections/Humans.Expenses/Services/`, `src/Sections/Humans.Expenses/Contracts/HoldedExpenseOutboxJob.cs`, `src/Sections/Humans.Expenses/Controllers/ExpensesController.cs`, any new admin route that touches IBAN.
