---
name: "@nobodies.team accounts are need-based, not a free-for-all"
description: "@nobodies.team accounts are granted only where there's an actual use — the role needs it (coordinating, externally facing) or the person handles other humans' PII. Copy/docs must never frame them as something everyone gets, and must caution about the responsibility: required 2FA, becomes the primary account for all Humans actions, two-inbox burden."
---

`@nobodies.team` Workspace accounts are **granted on need, never by default**: the human's role requires one (coordinating, externally facing — ticketing, comms, production) or they access other humans' personal data. Docs, UI copy, and features must never present them as a perk everyone gets or a self-service request.

Any copy describing them must also carry the caution: required 2FA (real sign-in friction), the address becomes the person's **primary account for everything done through Humans** (Google service email → all Drive/group access flows through it), and it means running two email accounts side by side (Chrome handles this well, but people not used to juggling accounts should be wary).

**Why:** Peter, 2026-06-11 (guide accuracy review): «this isn't a free for all. They should be granted to those having an actual use for them, either in their role, or because they're accessing pii. … it should be cautioned to the responsibility (and 2fa pain) that comes with it. Having one makes it the primary account for all humans actions as well.»

**How to apply:**

- Guide/UI copy: "granted where a role needs one", never "everyone gets".
- Coordinator-facing provisioning copy reminds: provision on need, point the human at the responsibility caution first (docs/guide/EmailAccount.md "Before you take one on").
- Don't build self-service request flows for these accounts without Peter's sign-off.
- Related: [[humans-terminology]].
