---
name: Analyzer exceptions: fix it, or tag the declaration
description: "HARD RULE. An architecture analyzer (HUM*) fires: fix the structure, never suppress or allowlist it. Existing debt gets `[Grandfathered]`; `[DontFix]` is Peter-only."
---

When a Humans architecture analyzer fires, fix the architecture: move the service to its section, break the DI cycle, add the missing interface member. If the structure is genuinely incoherent at that point, report it; the PR does not ship until it is fixed.

**Never silence one.** No `#pragma warning disable HUM…`, no `[SuppressMessage]`, no `// ReSharper disable`, no `.editorconfig` severity override, no baseline `.txt` entry, no allowlist of names inside the analyzer. No memory atom that rationalises a specific suppression either. Language-level warnings (CS0414 and the like) follow normal C# practice.

**The two attributes** (both in `Humans.Base.Attributes`), which live on the violating declaration itself:

| | `[Grandfathered(ruleId, justification, since, issueRef)]` | `[DontFix("reason", since)]` |
|---|---|---|
| Means | debt, will be fixed | correct here, never touch |
| Who adds it | agents may, to record debt that already exists | **Peter only.** If a rule seems wrong, report it |
| Analyzer output | Error becomes Warning | nothing |
| Debt runs | work it down | skip it |

- Fixing a grandfathered declaration: remove the attribute in the same commit.
- A new `[Grandfathered]` is for making existing debt visible, never for a violation you are introducing.
- `WarningsNotAsErrors` in `Directory.Build.props` lists rule ids that have grandfathers. That is rule-level config and fine; a list keyed by class or file is what's banned.
- Writing an analyzer: read `[Grandfathered]`/`[DontFix]` with the matching rule id off the symbol; emit Warning for the first, nothing for the second.

**Why:** centralised exception lists conflict on every merge and hide the debt away from the code. A suppression defeats the rule it silences.

**Related:** [`surface-budget-owner-applied`](../code/surface-budget-owner-applied.md) · [`brief-before-retiring-guardrails`](brief-before-retiring-guardrails.md) · [`our-rules-are-movable`](our-rules-are-movable.md)
