---
name: Stripe production keys must be Restricted API Keys (rk_*), never full secret keys
description: HARD RULE. When wiring any Stripe integration, the production env-var holds a `rk_live_*` RAK with the minimum scopes the integration uses — never `sk_live_*`. Test mode (`sk_test_*` / `rk_test_*`) is fine for dev.
---

When configuring a Stripe API key for the Humans project in production, use a Restricted API Key (`rk_live_*`) scoped to exactly the operations the integration performs — never a full secret key (`sk_live_*`).

**Why:** Stripe's official guidance is that secret keys grant god-mode (refunds, payouts, charge modifications, customer writes, everything). A compromised secret key can move money out of the account before anyone notices. RAKs limit blast radius — a compromised RAK can do only what its scopes allow. For Humans specifically, **all money-out operations (refunds, payouts, chargebacks) are policy-bound to be Stripe-dashboard manual** — a human deliberately clicks something on stripe.com — so production keys should not even have those scopes.

**How to apply:** When adding a new Stripe-using env var, name it `STRIPE_<ACCOUNT>_KEY` (`STRIPE_TICKETS_KEY`, `STRIPE_STORE_KEY`, `STRIPE_BUSSES_KEY`) — one key per Stripe account, not per operation. Document the required scopes in `docs/sections/<Section>.md` so the deploy step can configure the RAK correctly. Test mode (`sk_test_*`) is acceptable in `.claude/settings.local.json` and dev shells because Stripe's test-mode keys cannot move real money. The `StripeStartupSmokeService` makes a low-risk read against each configured key at boot and warns if the expected scope is missing — Stripe does not expose programmatic introspection of RAK scopes, so the probe is positive-confirmation only.

**Related:** `docs/sections/Store.md` "Stripe Configuration", `architecture/refunds-manual-via-dashboard.md`.
