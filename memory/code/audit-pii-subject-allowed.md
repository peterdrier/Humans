---
name: audit-pii-subject-allowed
description: Audit entries may carry PII unmasked when the PII belongs to the entry's own subject
type: code
---

An audit entry may contain PII unmasked when that PII belongs to the **subject** of the entry — "Peter set Dani's IBAN to ES12…" is correct, because it is Dani's IBAN on Dani's audit row.

**Why:** Peter's ruling. Traceability of an on-behalf change is the whole point of the entry: if an admin types the wrong account number for a member, the only way to find out what went wrong is to keep what they typed. A masked value records that something happened and nothing about what.

**How to apply:** The exception is narrow — the value must belong to the person the entry is *about*, and the entry must record somebody else acting on them. A member changing their own data keeps the masked convention unchanged (nobody needs to trace it; they are the actor and the subject). This is the one carve-out from [`iban-mask-in-logs`](iban-mask-in-logs.md), and it does not extend to logs or error messages — audit only. Live example: `ExpenseReportService.DescribeIbanChangeAsync`.
