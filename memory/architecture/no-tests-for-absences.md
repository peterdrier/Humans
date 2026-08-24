---
name: no-tests-for-absences
description: never write a per-section test asserting a section does NOT have something — no repository, no DbContext, no Hangfire dependency, no public type beyond N, no nav property, no ctor parameter; an absence has no behaviour to regress, the list of things a section lacks is unbounded, and the test can only fail on the deliberate edit that was going to update it anyway. Repo-wide universal enforcers are exempt; an analyzer beats a test wherever one can express the rule
---

Never write a per-section test whose assertion is that a section *lacks* something. The category is
irrelevant — repository, `DbContext`, Hangfire, `UserManager`, `IServiceProvider`, a nav
property, a ctor parameter, a public type, an assembly reference, a whole interceptor. If
the section doesn't have it, it doesn't have it. That's the whole story.

**Why:** Three reasons, each sufficient.

*Unbounded.* A section could plausibly have ~50 kinds of thing. A section that owns three
files could therefore carry 45 green "no xyz" tests, each green because nobody wrote the
code. That isn't coverage; it's a hand-maintained second copy of the file listing.

*No behaviour to regress.* Regression tests exist because behaviour drifts silently.
Nothing drifts here.

*It fails the wrong person.* An absence test inspects one declaration site and can only go
red when someone deliberately edits that exact site — adds the ctor parameter, adds the
method, marks the type public. That person is already looking at the thing and updates the
test in the same commit. The test never warns anyone; it just adds an edit.

*And some are testing for the impossible.* Since G5 each section is its own assembly with its
own `DbContext`, and repositories and entities are internal. A cross-section navigation
property or an out-of-section repository consumer cannot compile, so the seven per-section
nav-property tests and the seven `I*Repository_HasNoUnexpectedConsumers` were pinning a
historical hazard the structure had already closed (Peter, 2026-08-24).

**The trap that generates these:** a section doc claims an architecture test enforces
something and the test doesn't exist. The doc is wrong. Delete the false claim — do not
write the test to make the sentence true. A doc sentence is not a specification. This
shipped twice from `/section-doctor` runs: nobodies-collective/Humans#1465 (Onboarding, four
tests written, deleted again here) and peterdrier/Humans#1480 (Development, caught before
the write).

**How to apply:**
- About to assert `BeEmpty()` / `BeNull()` / `NotContain()` / `BeEquivalentTo([…])` over what
  a section's types, constructors, dependencies, entities or exports *are not*? Stop.
- Doc claims a pinning test that isn't there? Fix the doc, not the test file.
- Genuinely need it enforced? Enforce it **once, universally**, keyed off convention — never a
  per-section test. An analyzer whenever one can express the rule (Peter's hard rule: analyzers
  beat tests for call-site rules, because they give in-editor feedback and a source location);
  a single repo-wide reflection test only where an analyzer can't reach.
  See [`universal-enforcement-over-per-section`](universal-enforcement-over-per-section.md).
- Deleting one already in the tree: retire it with a note saying why the premise died, the
  way `AuditLogArchitectureTests` retired `SectionReferencesNoVerticalSection` — "re-listing
  the leaves would restate whatever the csproj happens to say, which is not an invariant."

**Universal enforcers are exempt.** This rule is about *per-section* tests. The sanctioned
repo-wide reflection tests in `tests/Humans.Web.Tests/Architecture/Rules/` — e.g.
`ApplicationServicesTakeNoDbContextRule`, which sweeps every section's services — assert an
absence and stay. The reasoning inverts at that scope: one test covers 40 sections' worth of
authors who have never seen it, so it fires on someone who wasn't thinking about the rule.
Never delete one of those under this atom; the most it justifies is asking whether an
analyzer could replace it.

**Not this rule** — these assert something the code actively does, and stay:
- A query returning no rows for an input that should match nothing.
- A localizer *binding* check: a misbind renders the raw key with a green build and no log
  line. Real defect, caught twice in Onboarding.
- An authorization negative: this role gets `404`/`AccessDenied` on this route.
- **Containment of something the section really has** — the section references the Stripe
  SDK / Google SDK / Octokit, and the test pins which layer may name it. A new method
  returning a vendor type is a realistic accident, and it fails whoever wrote it.
- **A privacy guard on data the section already holds.** `SurveyInvitation` has no
  completion timestamp because one would correlate with the anonymous response's
  `SubmittedAt` and re-identify the respondent; Cantina's roster DTOs carry no medical
  field because the service holds `ProfileInfo.MedicalConditions` already. The absent
  column is one property away from existing, and the harm is disclosure, not shape.
  Judge these as security constraints, not architecture.

The test: does the code *do* the thing you're checking, or are you checking that code was
never written?

**Related:** [`universal-enforcement-over-per-section`](universal-enforcement-over-per-section.md)
(a per-class "is-not-present" tombstone is called out there as a smell in its own right).
