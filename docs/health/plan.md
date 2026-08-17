# Section Doctor — Plan

No standing plan yet. Both runs so far were `--section` invocations; the first scheduled run
will write the 5–7 day table here.

## Needs Peter

Unticked items are open. `resume` works this list for merged runs, and the PR body for runs whose
PR is still open.

### 2026-08-17 — Guide (peterdrier/Humans#1354)

- [ ] **`GuideFiles.TryCanonical` shipped without the second-opinion gate.** Four attempts across
      three reviewer agents all went idle without a verdict; I worked their checklist myself (one
      concept; behaviour identical on null/empty/whitespace; no seam lost, all ten call sites
      passed the same set) but that is self-review. Judge it, or say revert — nothing depends on it.
- [ ] **The reviewer gate could not be obtained at all this run.** Should Phase 4.4 keep a
      subagent reviewer, or move to a fallback the main thread runs and labels as self-review?
- [ ] **Filter markdown instead of HTML?** The structural cure for Guide's fail-open class: cache
      markdown, drop blocks the viewer cannot see, then render. Removes `GuideFilter`'s regex and
      the `data-guide-*` attributes. Blocked only by the cache holding rendered HTML, which at 28
      files and ~500 users is not a real constraint. Worth an issue?
- [ ] **Should `NotFound` / `Unavailable` render the sidebar?** They reserve its column and leave
      it empty, so a mistyped guide URL strands the reader with one link home.
- [ ] **Is a low reforge score being read as "healthy"?** Guide scores 8 — lowest of any section —
      and was serving an admin block to anonymous visitors. The replan rubric ranks by score growth
      and staleness and would never have scheduled it. Should the rubric change?
