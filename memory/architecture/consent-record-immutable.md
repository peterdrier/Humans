---
name: ConsentRecord is database-immutable — INSERT only
description: The `consent_records` table has DB triggers preventing UPDATE and DELETE. Only INSERT. New consent state = new row. Required for GDPR audit trail integrity.
---

The `consent_records` table has database triggers that prevent UPDATE and DELETE operations. Only INSERT is allowed.

**Why:** GDPR audit trail integrity. Every consent decision must be reconstructible from the historical record — mutating or deleting a row would erase evidence of when a user gave/withdrew consent, which is the whole point of the table.

**How to apply:**

- New consent state = new row. Never mutate an existing row.
- Repository surface for `IConsentService` exposes `AddAsync` / `GetX` only — no `UpdateAsync` or `DeleteAsync`.
- Don't add `IsConcurrencyToken()` or audit fields that imply mutability — they make no sense for an append-only table.
- Don't try to "soft-delete" by toggling a field — same reason.

**Related:** [`design-rules.md §12`](../../docs/architecture/design-rules.md#12-immutable-entity-rules) — full immutable-entity inventory.
