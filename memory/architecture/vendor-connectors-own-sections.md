---
name: Vendor connectors stay their own sections — never merged with app code
description: HARD RULE. External-vendor connector sections (Holded, MailerLite, GoogleIntegration) are never merged into the app sections that consume them — vendors can be swapped. (Peter, 2026-08-03 inventory freeze.)
---

A section that wraps an **external vendor** (Holded accounting, MailerLite, Google Workspace via GoogleIntegration) stays its **own section**, even when a single app section is its only consumer. Vendor connectors are never merged with application code.

**Why:** Vendors can change. Finance is likely Holded's only user, but folding the Holded connector into Finance would weld a replaceable vendor surface into domain code. Peter, at the 2026-08-03 inventory freeze: "that's an external vendor which could change … vendors don't get merged with app code ever." Same ruling for MailerLite vs Email.

**The target shape** (Peter, 2026-08-09): a connector section owns its typed client, **proxies what
the app needs, and caches vendor responses as necessary** — GoogleIntegration is the reference.

**How to apply:**

- Don't propose merging a connector section into its consumer section during inventory/architecture cleanups.
- New vendor integrations get their own section, consumed through its interface — not code inside the consuming section.
- Consumer sections talk to the connector's interface; swap-ability is the design goal.
- **A connector does not get absorbed into the domain section that uses it.** `IHoldedClient`,
  `HoldedClient`, `HoldedClientOptions` and the Holded DTOs live in `Humans.Holded` — the
  interface and DTOs public on `Humans.Holded.Contracts`, the client `internal` in the section.
  Stripe is the same shape: **`src/Sections/Humans.Stripe`** — `IStripeService` under its
  `Contracts/` folder, the SDK and both hosted services `internal` behind it, consumed
  by Store and Tickets through a direct project reference.
- **Never re-export a vendor's wire DTO across a section boundary.** It makes every downstream
  consumer a consumer of the vendor's API shape, and it cycles the section's contracts leaf besides
  (see [`section-project-cycle-fix`](section-project-cycle-fix.md)). Own a boundary type and map at
  the edge — Finance's `CreditorLedgerLine` in place of the connector's `HoldedLedgerLineDto`.
- **Cache tables follow the connector; decision tables stay with the domain.** For Holded that is
  `holded_ledger_lines` + `holded_expense_docs` (read-through cache of vendor facts → Holded) versus
  `holded_category_map` + `holded_creditor_contacts` (our attribution rules and member bindings →
  Finance).
- **Don't rename vendor-prefixed tables before the connector section exists** — the right name
  depends on which side the table lands on. Tracked in nobodies-collective/Humans#1012.
- A vendor prefix does not prove ownership: `holded_expense_outbox_events` is **Expenses'**, not
  Finance's and not Holded's.

**Related:** [`sections-are-logical-units`](sections-are-logical-units.md), [`section-project-cycle-fix`](section-project-cycle-fix.md), [`team-resources-google-integration-section`](team-resources-google-integration-section.md).
