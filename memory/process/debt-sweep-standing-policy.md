---
name: Debt sweep standing policy — what is pre-approved, what is not debt, how to ask
description: Unattended debt runs (nightly sweep, /debt-sweep, /debt-review) — rule-prescribed contract changes are pre-approved; what closes outright, what is not debt, how a Needs Peter item is written.
---

Drain the ledger unattended. A shape an existing rule already prescribes is approved by that rule; a row is skipped only after a code check; a question to Peter is answerable cold.

**Why:** PR peterdrier/Humans#1908 punted ~60 rows by category and wrote "public surface needs approval" into rows it created, so each night read its own clause as the blocker. Its Needs Peter list was bare row ids. Peter, 2026-10-06: a row id like "CENTRAL-26" is never enough for him to know what it's about; "Needs Peter" items must be answerable cold.

**How to apply:**

- **D1 Pre-approved (rule is the approval).** When a `memory/` atom or docs rule already prescribes the shape, do it: throw→result record ([`no-localized-exception-messages`](../code/no-localized-exception-messages.md)); optional parameter or tightened signature on an EXISTING method; scalar field on an EXISTING DTO; `I<Section>ServiceRead` split ([`section-read-write-split`](../architecture/section-read-write-split.md)); pattern constant in `DateFormattingExtensions` ([`datetime-format-single-home`](../architecture/datetime-format-single-home.md)); one owner-section method replacing ≥2 identical cross-section caller sequences; section-internal split: moving a self-contained job out of an oversized service into a new `internal` class in the same section, no new Contracts interface (Peter 2026-10-06); adding a member to an existing enum (e.g. `AuditAction`), treated like adding a field to an existing DTO (Peter 2026-10-06). List each in the PR body under **Pre-approved contract changes** so Peter can veto. NEW interfaces, services and endpoints still go to Peter.
- **D2 "Public surface"** = `public` members of a `Contracts/` folder or `.Contracts` leaf, and of Base, consumed by another project. `internal` types, view models/builders, view-component ctors and `internal` interfaces are never surface.
- **D3 Needs Peter format.** Self-explanatory to someone who has NOT read the ledger: what the code does today, what would change, why it matters, then a yes/no proposal with a concrete signature. The row id alone is never sufficient. Never write "needs approval" into a row's `what:`.
- **D4 No category-bin skips.** Every skipped row gets a one-line code check that night; a failed check routes the row to close.
- **D5 Close outright, no question:** code no longer exists; duplicates a theme or row; the row is itself a CI/check/gate proposal ([`no-new-ci-checks`](no-new-ci-checks.md)); perf concern with no budget at this scale; Contracts-folder-vs-leaf preference. Merge symptom rows under their `root:`.
- **D6 Not debt — pinning tests.** Tests that merely pin behaviour (controller-policy pins, "posture" pins, missing-controller-test inventories) — Peter: "tests that pin things have gone past a reasonable line, solidifying what doesn't need to be solid." Don't add such rows. Integration-test flakiness is a GH issue, not a row (only Peter runs them).
- **D7 Not debt — judgment inventories.** "We don't analyze away common sense": no analyzers/inventory checks for judgment calls (e.g. GDPR-contributor inventory). Analyzers stay for mechanical architecture rules (e.g. no EF types in Contracts).
- **D8 Not debt — admin-timed races and slow admin pages.** A race that needs an admin to act at a bad moment (clear cache during startup warmup), or an admin-only page that is slow but works.
- **D9 Not debt — destructive schema work.** Column drops, FK removals are feature work → GH issue, never a ledger row.
- **D10 Seeds are re-derived.** Ladder seeds/counts come from the detectors each night; fix stale ladder text in the same PR.
- **D11 Theme bookkeeping.** Add a missing `parked:` key where notes cite Peter; delete retired themes with no analyzer to re-seed them.
- **D13 Hangfire — just delete shims.** Peter: "Hangfire jobs all register themselves at startup; we do no Hangfire migrations, ever." If an assembly/type moves, the job re-registers under the new name on next boot. No exceptions, including the signatures of methods reached through Enqueue/Schedule: a job still queued across a deploy is dropped, not shimmed (Peter, 2026-10-07; retires the former hangfire-method-signature-stable atom). Never write Hangfire compat shims, type remaps or migrations; delete any such shim on sight.
- **D12 `[Grandfathered]` exception.** A new one is allowed when it makes already-existing debt visible to an analyzer; say so in the commit message.

**Related:** [`reuse-first-change-discipline`](reuse-first-change-discipline.md), [`../architecture/interface-method-additions-are-debt.md`](../architecture/interface-method-additions-are-debt.md), [`debt-ledger-must-shrink`](debt-ledger-must-shrink.md), [`debt-ledger-additions`](debt-ledger-additions.md)
