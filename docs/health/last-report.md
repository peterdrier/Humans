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

## Lanes that did not report

Four subagents (tests/Stryker, InspectCode, docs, and two successive opus reviewers) were
dispatched and **none returned within the run**, despite status pings. So this run has:

- **no mutation score** — Stryker never reported; the config the lane left behind was deleted
  rather than committed unvalidated;
- **no InspectCode pass**;
- **no independent second opinion** on the `TryCanonical` consolidation. I worked the reviewer's
  own checklist against it instead (one concept, behaviour identical on every input including
  null/empty/whitespace, no seam lost since all ten call sites passed the same set) and shipped
  it, but that is self-review and is flagged in the PR for Peter to judge.

Everything reported above was found and verified directly, not via a lane.

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
- **Process risk this run exposed:** every finding came from the one thread doing the work, and
  the four parallel lanes contributed nothing. A run that had delegated more would have shipped
  less. Worth watching whether the lane pattern is paying for itself.
