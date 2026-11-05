# look-audit

Captures every page of the Humans web app on QA (desktop 1440x900, mobile 390x844 for non-admin pages): screenshots, layout metrics, axe results. Scores are added by reviewers (or agents) from the screenshots, then everything is exported to one spreadsheet.

Env (never commit or log the login URL):
- `LOOK_AUDIT_LOGIN_URL` GET URL that signs the browser in as an admin (required)
- `LOOK_AUDIT_BASE_URL` default `https://humans.n.burn.camp`

Flow (run from `tools/look-audit`, `npm install` once; playwright + chromium come from the global install):
1. Routes: `dotnet run routes/Routes.cs -- <abs Humans.Web bin dir> <abs path>/out/routes.json`
2. Capture: `node capture.mjs [--run <id>] [--only <substr>] [--limit N] [--routes file]` writes `out/<runId>/pages.json` and `shots/`. Resumable with `--run <id>`. `routes.sample.json` is a tiny smoke-test input.
3. Score: `look-scorer` subagents (`.claude/agents/look-scorer.md`, Sonnet) score batches of pages against `rubric.md` in two independent passes (`scores-a/`, `scores-b/`); `node merge-scores.mjs <runId>` takes the median into `scores/` and lists pages whose passes disagree by more than 1 point, which get a third pass.
4. Export: `node build-xlsx.mjs [runId]` writes `out/<runId>/look-audit.xlsx`.

Side-effect-looking routes are skipped (`SKIP_RE` at the top of `capture.mjs`); templates with params are filled from links found on visited pages, else marked `no-sample`.

`out/` holds screenshots with real member data: never commit, attach or publish it.
