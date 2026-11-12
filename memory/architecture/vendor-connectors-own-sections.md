---
name: vendor-connectors-own-sections
description: HARD RULE. Vendor connector sections (Holded, MailerLite, GoogleIntegration) are never merged into their consumer sections — vendors can be swapped; never re-export a vendor's wire DTO.
---

A section wrapping an external vendor (Holded, MailerLite, Stripe, Google Workspace via
GoogleIntegration) stays its own section, even when one app section is its only consumer.

**Why:** Peter: *"that's an external vendor which could change … vendors don't get merged with app code
ever."* Folding the connector into its consumer welds a replaceable surface into domain code.

**The shape:** the connector owns its typed client, proxies what the app needs and caches vendor
responses as necessary. GoogleIntegration is the reference.

**How to apply:**
- A new vendor integration gets its own section, consumed through its interface.
- **The raw client is `internal`.** Other sections read through the connector's service
  (`IHoldedService`, `IStripeService`) and write through its write interface (`IHoldedWrite`).
- **Never re-export a vendor's wire DTO across a section boundary.** It makes every consumer a consumer
  of the vendor's API shape. Own a boundary type and map at the edge (Finance's `CreditorLedgerLine`,
  not the connector's ledger DTO).
- **Cache tables follow the connector; decision tables stay with the domain.** A read-through cache of
  vendor facts belongs to the connector; our own attribution rules and member bindings belong to the
  domain section.
- A vendor prefix on a table name does not prove ownership (`holded_expense_outbox_events` is
  Expenses').
- Never propose merging a connector into its consumer during a cleanup.

**Related:** [`sections-are-logical-units`](sections-are-logical-units.md) ·
[`section-project-cycle-fix`](section-project-cycle-fix.md) ·
[`team-resources-google-integration-section`](team-resources-google-integration-section.md)
