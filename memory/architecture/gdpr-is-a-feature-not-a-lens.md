---
name: GDPR is a feature we implement, not a lens to audit our own code through
description: HARD RULE — never argue that our own in-process code reading personal data is a GDPR concern. Metrics, snapshots, caches, full-population reads: not GDPR questions. The GDPR features are consent gates, export, and erasure; "this caller sees more personal data than it needs" is not a finding.
---

We own 100% of this code and we trust ourselves with it. Personal data moving between our own services, caches and snapshots in one process is not a data-protection event.

**Why:** GDPR in Humans is a set of *features* — consent tracking and its gates, the data-export contributors, the right-to-deletion fan-out. Those are real, they are load-bearing, and keeping them whole is non-negotiable (see `AGENTS.md`, "GDPR is a feature, not a checkbox"). What it is not is a review lens to point at ordinary internal code. A metrics service that loads every `UserInfo` to count how many need consent review is doing arithmetic, not processing a subject's data unlawfully.

**How to apply:**

- Never write, raise, or escalate a finding of the form "this caller receives more personal data than it needs, which is GDPR surface." Not as a finding, not as a caveat, not as one bullet in a list of otherwise-valid reasons. Peter's words: "DO NOT WORRY ABOUT METRICS DOING SOMETHING that's a gdpr violation. period. never bring that up. it's not a valid concern full stop."
- In particular this covers: full-population reads (`GetAllUserInfosAsync`), in-memory snapshots and caches holding whole `UserInfo` records, a section reading a field it does not strictly need, and wide DTOs crossing a section boundary.
- Where a boundary argument is genuinely worth making, make it on its own merits — Users' predicates drifting when reimplemented elsewhere, a caller wanting a count and being handed a population, width as a cost. Those stand alone. Do not reach for GDPR to add weight to them; doing so makes a sound argument dismissible.
- **Still in scope, and unchanged:** the consent gates in front of what they gate, an export contributor for new personal data, a deletion path for new personal data, and anything that sends personal data *out* of the system — a downloaded file, an email, a third-party API call, a log line. Leaving the building is the line; moving between our own objects is not.

Related: [`every-human-has-an-email`](every-human-has-an-email.md) — the same genre of rule, closing off a class of false-positive findings.
