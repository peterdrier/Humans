---
name: iban-mask-in-logs
description: All IBAN output to logs / audit / errors goes through IbanFormatter.Mask
type: code
---

<!-- freshness:triggers
  src/Humans.Infrastructure/Jobs/HoldedExpenseOutboxJob.cs
-->
<!-- freshness:flag-on-change
  Rule: iban-mask-in-logs
  Flag if a symbol, path, namespace or behavior this rule names has changed.
-->

All IBAN output to logs / audit / errors goes through `IbanFormatter.Mask`.

**Why:** Spanish data protection + GDPR; raw IBAN is personal financial data.

**How to apply:** When you touch any log statement that references `Profile.Iban` or `ExpenseReport.PayeeIban`, wrap the value in `IbanFormatter.Mask(...)`. The only legitimate places raw IBAN appears are inside the `<IBAN>` element of the SEPA pain.001 XML and in the body of an outgoing Holded API request.

**Surface area:** `src/Humans.Application/Services/Expenses/`, `src/Humans.Infrastructure/Jobs/HoldedExpenseOutboxJob.cs`, `src/Humans.Infrastructure/Jobs/ExpensePaidPollingJob.cs`, `src/Humans.Web/Controllers/ExpensesController.cs`, any new admin route that touches IBAN.
