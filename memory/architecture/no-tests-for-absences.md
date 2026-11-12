---
name: no-tests-for-absences
description: Never write a per-section test asserting a section lacks something (no repo, DbContext, nav property) — absence has no behaviour to regress. Repo-wide universal enforcers are exempt.
---

Never write a per-section test whose assertion is that a section *lacks* something: a repository,
`DbContext`, nav property, ctor parameter, public type, assembly reference, anything.

**Why:** The set is unbounded (a three-file section could carry 45 green "no xyz" tests). Nothing drifts,
so there is no behaviour to regress. It only goes red for the person deliberately editing that exact
declaration, who then updates the test in the same commit. And several were testing the impossible:
sections are separate assemblies with internal repositories and entities, so cross-section reach cannot
compile.

**Nor is an absence a question for Peter.** *"if there's no tests, no folder, don't look for things that
don't need to exist. absence isn't a problem."* A missing thing goes in a Needs-Peter queue only with a
concrete failure attached.

**How to apply:**
- About to assert `BeEmpty()` / `BeNull()` / `NotContain()` over what a section's types, constructors,
  dependencies or exports *are not*? Stop.
- A section doc claims a pinning test that doesn't exist? The doc is wrong: delete the claim, don't write
  the test. A run that withdraws one of these re-reads every test it added in the same pass.
- Genuinely need it enforced? Do it once, universally: an analyzer where one can express it, otherwise a
  single repo-wide reflection test —
  [`universal-enforcement-over-per-section`](universal-enforcement-over-per-section.md).
- Deleting one already in the tree: leave a note saying why the premise died.

**Universal enforcers are exempt.** The repo-wide rules in `tests/Humans.Web.Tests/Architecture/Rules/`
(e.g. `ApplicationServicesTakeNoDbContextRule`) assert an absence and stay: one test fires on authors
across every section who never saw the rule. Never delete one under this atom.

**Not this rule — these test something the code does:**
- A query returning no rows for input that should match nothing.
- A localizer binding check (a misbind renders the raw key on a green build).
- An authorization negative as a *request* against the route asserting the deny. Reading an
  `[Authorize]` attribute by reflection is the forbidden shape wearing an authorization label.
- Containment of something the section really has: it references a vendor SDK and the test pins which
  layer may name it.
- A privacy guard on data the section already holds (`SurveyInvitation` has no completion timestamp
  because it would re-identify the anonymous response; Cantina roster DTOs carry no medical field).
  Judge these as security constraints.

The test: does the code *do* the thing you're checking, or are you checking that code was never written?
