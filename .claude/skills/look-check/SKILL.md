---
name: look-check
description: "Look at rendered Humans pages: screenshot them at desktop and phone width, score them against the look rubric, list concrete problems. Use after changing a view and before calling UI work done. Argument: the paths to check, or a PR number."
argument-hint: "<path...> | <pr number>"
---

# Look check

Advisory: a low score never blocks a merge. It tells the builder to try again while there is time.

## 1. Find somewhere to look

- **PR preview** (preferred): `https://<pr>.n.burn.camp`. `GET /api/version` first; a plain 404 means no preview was deployed, so fall back to local.
- **Local**: build, start Postgres, and run the Shell in Development on `http://localhost:5080` (`dotnet run --no-launch-profile --project src/Humans.Web --urls http://127.0.0.1:5080`, with placeholder `Authentication__Google__ClientId` / `ClientSecret`). `POST /dev/seed/dashboard` from `/dev/seed` creates an event. A page gated on vendor configuration needs a placeholder setting; never a real key, and never press a pay button.
- QA has no dev login, so it cannot be used.

## 2. Capture

From `tools/look-audit` (`npm install` once):

```bash
LOOK_BASE_URL=https://<pr>.n.burn.camp NODE_PATH=$(npm root -g) \
  node shoot.mjs <outName> <persona|anon> buy=/Tickets/Buy sales=/Tickets/Admin/Sales
```

It signs in through `/dev/login/<persona>` (`admin`, `volunteer`, ...), writes `out/<outName>/<name>-{desktop,mobile}[-fold].png` and `report.json` (HTTP status, where the page landed, sideways overflow, serious axe findings). Check the status and landing URL: a redirect to sign-in or an error page is not the page.

## 3. Look and score

Read `tools/look-audit/rubric.md`, then Read each page's fold and full screenshots. Score every metric 1 to 5 per width (no mobile score for admin-shell pages) against Peter's anchors in the rubric: is this calmer or busier than `/Budget`? List at most five problems per page, worst first, tagged `bug` / `ux` / `polish`, each naming the element. Compare against `docs/design/DESIGN.md`: where is the page title, what is the most prominent thing, where is the primary action?

## 4. Act

- Overall below 3, or any `bug`: fix and run again, up to three rounds.
- Serious axe findings on elements the change added or touched: fix. Findings in the chrome or on untouched elements: report, don't fix here.
- Report a small table (page, width, overall, top problems) with before and after when there was a before.

## Rules

- Preview screenshots show real member data cloned from QA: never commit, attach or publish them, and never quote names or emails from them. Local seed-data screenshots may be shared.
- `out/` is git-ignored; leave it that way.
