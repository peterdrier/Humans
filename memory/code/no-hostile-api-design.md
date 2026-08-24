---
name: no-hostile-api-design
description: Never propose deliberately awkward or ugly method names as a way to discourage callers — restriction is enforced by visibility and call-site pinning, never by punishing the name.
---

Never propose deliberately awkward, verbose, or ugly API names (e.g. `RewriteEmailFromOAuthCallbackAsync`, `DoNotCallThisExceptFromXAsync`) as a strategy to discourage misuse. Names describe what the method does, period.

**Why:** Peter pushed back hard on exactly this kind of rename once. "Make the signature obviously not for general use" is anti-design — it punishes legitimate callers and pollutes the call site as a substitute for proper enforcement.

**How to apply:** when the goal is "only X may call Y," the right tools are:
- **Visibility** — `internal` + `InternalsVisibleTo`, or moving the method behind a narrower interface only X depends on.
- **Enforcement** — a Roslyn analyzer or arch test that pins call sites.
- **Documentation** — an XML doc on the method explaining the contract.

NOT awkward names. A method named `UpdateEmailAsync(provider, providerKey, newEmail)` is fine — clear, ordinary, does what it says. Restriction is enforced by visibility and call-site pinning, not by making the name punish you for typing it.
