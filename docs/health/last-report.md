# Section Doctor — Last Run Report

**Run:** 2026-08-17, Guide (`--section=Guide`, no plan existed). Anchor `acb86a911`. Budget 2.5h.

## Assessment summary

Guide is structurally the cleanest section in the codebase — reforge 8, the lowest of any
section, with its only findings being `Wrap`'s complexity and one legitimate cross-section read
interface. Everything is `internal`, it owns no tables, and its documented deviations carry real
rationale. There was no structural work to do.

The defects were somewhere the structural metrics cannot see: **the section's access control is
enforced by regex over rendered HTML, and it fails open.** Both bugs found this run are the same
shape — content the pipeline does not recognise is served *unfiltered* rather than rejected —
and neither was visible to unit tests, to grep, or to any score. Both were found by running the
real shipped `docs/guide/` content through the real pipeline. Full scorecard:
`src/Sections/Humans.Guide/Docs/health.md`.

## Worked

- **`docs/guide/Feedback.md` was serving its admin block to anonymous visitors.** The
  preprocessor only wraps `## As a <Volunteer|Coordinator|Board>`; the page used `## As an
  Admin`, so the entire triage section — queue screens, assignment, status workflow — was never
  wrapped in a `data-guide-role` div and `GuideFilter` had nothing to strip. Heading brought to
  the convention every other page uses; new test runs all 28 shipped files through `Wrap()` and
  fails on any unwrapped `As a …` heading. (`d8a000ba4`)
- **Events Admin and Store Admin could not see the blocks addressed to them.**
  `GuideRolePrivilegeMap` maps both parentheticals, but `GuideRoleResolver` kept a *second*,
  hand-written list of roles to probe against claims and neither was in it. The resolver now
  derives from the map, so the halves cannot drift; new theory pins every mapped role.
  (`217e75b36`)
- Dead `src/Humans.Infrastructure/**` freshness triggers (project deleted at G5 lane 5b-6)
  repointed at `Humans.Interfaces`, and the same claim swept out of the feature spec, the
  Contracts README and the csproj comments. (`b737d1822`)
- `IGuideContentService.RefreshAllAsync` was documented as evicting all cached entries. It never
  evicts — it overwrites, and a file that fails to fetch keeps its old copy, which is the entire
  reason a GitHub outage degrades to stale content instead of an empty guide. Doc now says that;
  a test comment describing a "populated flag" the service does not have went with it.
- `GuideFilter`'s "no nested divs" comment was load-bearing and unchecked — a Markdig footnote
  or `:::` container inside an admin block would truncate the block and leak its tail. Now
  pinned against every shipped page. (`ddb4e6b3b`)
- `GuideFiles.TryCanonical` folds three duplicated case-insensitive stem scans into one owner;
  `GuideHtmlPostprocessor.Rewrite` loses a parameter every caller passed identically. Section's
  first controller tests cover the 404 and 503 paths and the casing resolution.
- Prior run's log row backfilled to peterdrier/Humans#1341 and its maintenance-log row carried
  forward — both existed only on the unmerged #1341 branch.

## Skipped / queued

- **Filtering markdown instead of HTML** — the one substantial move available, and the
  structural cure for the whole fail-open class. Needs Peter: it changes the cache unit from
  rendered HTML to markdown. Ranked #1 in `health.md`.
- nobodies-collective/Humans#1035 (`(Camp Coordinator)` resolves to nothing) — the other half of
  the resolver bug's shape, but a privilege decision rather than a drift repair. Left filed.
- Dead `src/Humans.Infrastructure/**` trigger globs in six *other* sections' `docs/guide` pages —
  nobodies-collective/Humans#1021's scope, off-section, not chased.
- `NotFound` / `Unavailable` render no sidebar and strand the reader. Left as an opportunity
  rather than a UI change during a doctor run.

## Lanes

All five subagents (tests/Stryker, InspectCode, docs, two opus reviewers) missed the run window;
everything above was found and verified directly. The docs and tests lanes reported **after** the
PR was opened, and a second pass worked what they found:

- **Docs lane** independently reached the same `KnownRoles` conclusion (already fixed), and added
  one in-section find I had missed: `Contracts/README.md` still cited `Humans.UI`'s
  `_LoginPartial`, and `Humans.UI` was deleted in `9830ba3ed`. Fixed. It also measured the
  freshness-trigger rot far beyond Guide — **~113 dead trigger lines across 16 of the 28 guide
  pages** — and caught `docs/guide/Expenses.md` missing today's #1336 feature entirely. Both to
  the debt ledger; both off-section.
- **Tests lane** confirmed the `RefreshAllAsync_ClearsAndRefetches` naming problem I had already
  fixed, and its invariant matrix found three genuine gaps I had not: the **`POST /Guide/Refresh`
  admin-only rule had no test at any level**, `GuideFiles` was never checked against the files on
  disk, and every Board/Admin filter test also contained a `boardadmin` block, so the superset
  promotion masked `IsCoordinatorVisible`'s own Board/Admin grant. All three now covered.
- **Still missing: no mutation score** (Stryker never reported; its unvalidated config was
  deleted rather than committed), **no InspectCode pass** (the lane confirmed `jb` works and ran
  clean once, then re-ran for freshness and never sent final findings), and **no independent
  second opinion** on the `TryCanonical` consolidation. That gate was attempted four times across
  three agents — two `general-purpose` opus reviewers and one `feature-dev:code-reviewer` opus —
  each given a progressively shorter brief, down to "reply with exactly three lines, and say
  REJECT/insufficient-analysis if you did not finish". Every one went idle without answering,
  including after a direct follow-up. I worked the checklist myself and flagged it in the PR.

  Worth treating as a finding about the harness rather than the task: a gate that cannot be
  obtained is not a gate. Either the skill needs a fallback the main thread can execute and
  label honestly (what I did), or the reviewer step needs to stop being a subagent.

## Retro

- **What the rubric got wrong:** it ranks sections by reforge score and staleness. Guide scores
  8 — by that rubric it is the *last* section worth visiting, and it was hiding an
  anonymous-visible admin block. Structural cleanliness and correctness are close to
  uncorrelated here; the rubric should not treat a low score as evidence a section is healthy.
- **Wasted motion:** started a full `dotnet test` in the background and then ran a build against
  the same worktree — the test host held the output DLLs and the build burned five retry rounds
  on locked files. One build/test at a time per worktree.
- **What the assessment missed that striking revealed:** the whole content-shape class. The lanes
  were briefed to read *code*; nothing read the 28 markdown files the code exists to serve until I
  ran them through the pipeline. For a section whose input is content, the content is part of the
  section — now a standing lane in the skill.
- **Process risk this run exposed:** the lanes all missed the run window, so a run that had
  delegated more would have shipped less. But once they did land they were worth having — the
  tests lane's invariant matrix found an untested negative access rule, which is exactly the class
  of defect this run was about, and the docs lane measured a repo-wide problem I had only seen the
  Guide-sized corner of. The fix is a deadline and a second pass, not fewer lanes.
