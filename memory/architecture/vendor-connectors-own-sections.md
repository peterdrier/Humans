---
name: Vendor connectors stay their own sections — never merged with app code
description: HARD RULE. External-vendor connector sections (Holded, Mailer/MailerLite, GoogleIntegration) are never merged into the app sections that consume them — vendors can be swapped. (Peter, 2026-08-03 inventory freeze.)
---

A section that wraps an **external vendor** (Holded accounting, MailerLite via Mailer, Google Workspace via GoogleIntegration) stays its **own section**, even when a single app section is its only consumer. Vendor connectors are never merged with application code.

**Why:** Vendors can change. Finance is likely Holded's only user, but folding the Holded connector into Finance would weld a replaceable vendor surface into domain code. Peter, at the 2026-08-03 inventory freeze: "that's an external vendor which could change … vendors don't get merged with app code ever." Same ruling for Mailer (MailerLite) vs Email.

**How to apply:**

- Don't propose merging a connector section into its consumer section during inventory/architecture cleanups.
- New vendor integrations get their own section, consumed through its interface — not code inside the consuming section.
- Consumer sections talk to the connector's interface; swap-ability is the design goal.

**Related:** [`sections-are-logical-units`](sections-are-logical-units.md), [`team-resources-google-integration-section`](team-resources-google-integration-section.md).
