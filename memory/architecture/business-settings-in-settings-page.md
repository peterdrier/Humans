---
name: Business settings live on /Settings, never in appsettings
description: Adding any value an admin may need to change (an account number, a threshold, an id mapping): it goes in the owning section's table and a /Settings tab, never an appsettings/IConfiguration key.
---

A value Peter or an admin decides (a Holded account, a limit, which Stripe account maps to which ledger account) is stored in the owning section's own settings table and edited on a tab of `/Settings`, contributed through `ISectionSettings`. Never an `appsettings.json` key, an `IOptions<T>` bound from configuration, or an environment variable.

**Why:** Peter, 2026-11-19, on a Store page error naming `Store:DepositLiabilityAccountNum`: "we have an entire configuration settings module in the application, wtf are you hard coding settings". A config key needs a deploy to change, is invisible to the people who own the value, and leaves no audit trail.

**How to apply:**
- One row (or a small keyed table) in the section's own DbContext, an EF-generated migration, a `<Section>SettingsTabViewComponent` gated on the section's Manage policy, a POST on the section's controller that redirects back to `/Settings#<tab>`, and an audit entry per save. Gate, Agent, Store and Finance are worked examples.
- An account choice is a picker over the Holded chart mirror, never a typed number.
- An error about an unset value names the Settings page tab, never a config key.
- Configuration is only for infrastructure: secrets, API keys and their variable names, base URLs, connection strings, call budgets.

**Related:** [[repository-required-for-db-access]]
