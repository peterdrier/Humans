---
name: no-speccing-from-thin-probe
description: When designing an external-API integration, do broad endpoint exploration and read full real-record JSON before drafting entities — never pattern-match from one endpoint's field names.
---

When designing an integration with an external API (Holded, Stripe, Google, anything), don't produce an entity model from a single endpoint's field-name list. That's pattern-matching, not investigation.

Required minimum before drafting entities:
- Probe ALL adjacent endpoints that might be in scope (other doc types, related resources, contacts, accounts, lookups).
- Get full JSON of at least one real record — not just field names; actual values reveal nullability, semantics, units.
- Test pagination, filtering, and any incremental-sync params planned for use.
- Verify mutating capabilities (PUT/POST) before designing flows that depend on them, or call them out as untested risks.
- Check semantic gotchas: an ambiguous field name might mean something different than assumed; date fields might be null on real data; status codes are usually undocumented.

**Why:** during a Holded integration brainstorm, an entity model was proposed after one GET returned 25 field names. Peter pushed back: "you're speccing out entities before having connected to the holded api.. so you're guessing.. not good." Broader probing then surfaced real surprises that would have wasted implementation time — a primary date field null on every real doc, a "tax" field meaning something narrower than VAT, an incremental-sync filter param that silently returned zero results, and a doc type referenced in the original issue that didn't exist at all.

**How to apply:** when starting an integration design: verify API access first, run a broad probe sweep across candidate endpoints and write raw findings to a local file, read those findings for semantic meaning (not just field-name extraction), THEN propose a data model with concrete grounding, and call out remaining unverified capabilities as explicit risks in the spec.
