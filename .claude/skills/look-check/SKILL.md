---
name: look-check
description: "Look at rendered Humans pages: screenshot them at desktop and phone width, score them against the look rubric, list concrete problems. Use after changing a view and before calling UI work done. Argument: the paths to check, or a PR number."
argument-hint: "<path...> | <pr number>"
---

# Look check

One look, one report. Advisory: a low score never blocks a merge. The caller decides whether to fix and look again (three looks in total is the house limit).

## 1. Find somewhere to look

- **While building: a local run.** Changes show without a push. Build, start Postgres, and run the Shell in Development on `http://localhost:5080` (`dotnet run --no-launch-profile --project src/Humans.Web --urls http://127.0.0.1:5080`, with placeholder `Authentication__Google__ClientId` / `ClientSecret`). `POST /dev/seed/dashboard` from `/dev/seed` creates an event. `POST /dev/seed/budget` fills `/Budget`, the reference page. A page gated on vendor configuration needs a placeholder setting; never a real key, and never press a pay button.
- **Final look: the PR preview**, `https://<pr>.n.burn.camp`. `GET /api/version` first: a plain 404 means no preview was deployed (common; stay local and say so), and its `commit` must match the pushed sha, or you are looking at the old build.
- QA has no dev login, so it cannot be used.

Given a PR number, the pages are the routes of the views the PR changed (`gh api repos/peterdrier/Humans/pulls/<n>/files`, then each view's controller action route).

## 2. Capture

From `tools/look-audit` (`npm install` once):

```bash
LOOK_AUDIT_BASE_URL=http://localhost:5080 NODE_PATH=$(npm root -g) \
  node shoot.mjs <outName> <persona|anon> budget=/Budget buy=/Tickets/Buy sales=/Tickets/Admin/Sales
```

It signs in through `/dev/login/<persona>` (`admin`, `volunteer`, ...), writes `out/<outName>/<name>-{desktop,mobile}[-fold].png` and `report.json` (HTTP status, where the page landed, sideways overflow, serious axe findings with the elements they sit on). Check the status and landing URL: a redirect to sign-in or an error page is not the page. Capture `/Budget` alongside as the reference point.

## 3. Look and score

Read `tools/look-audit/rubric.md`, then Read each page's fold and full screenshots. Score every metric 1 to 5 per width (no mobile score for admin-shell pages) against Peter's anchors in the rubric: is this calmer or busier than `/Budget`? List at most five problems per page, worst first, tagged `bug` / `ux` / `polish`, each naming the element. Compare against `docs/design/DESIGN.md`: where is the page title, what is the most prominent thing, where is the primary action?

## 4. Report

A small table (page, width, overall, top problems), with the earlier scores beside it when there was an earlier look. Then what to do:

- Overall below 3, or any `bug`: worth another round.
- Serious axe findings whose targets are elements the change added or touched: fix. Findings in the chrome or on untouched elements: report only.

## Rules

- Preview screenshots show real member data cloned from QA: never commit, attach or publish them, and never quote names or emails from them. Local seed-data screenshots may be shared.
- `out/` is git-ignored; leave it that way.
