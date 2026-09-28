---
name: "`Humans.Integration.Tests` is local-only — never propose running it in CI/cloud, never report it"
description: HARD RULE. `Humans.Integration.Tests` is opt-in (`HUMANS_INTEGRATION_TESTS=1`) and never runs in a default `dotnet test` — agents never opt in; never mention, investigate, count, or CI-gate it.
---

# `Humans.Integration.Tests` is local-only — never propose running it in CI/cloud, never report it

**Rule:** `tests/Humans.Integration.Tests/` is the home of tests that run **only in Peter's local environment** — they integrate with things that exist nowhere else. `build.yml`'s `--filter "FullyQualifiedName!~Humans.Integration.Tests"` is the design, not an oversight, not a stale Docker workaround, and not tech debt. Never propose adding a CI job for the project, moving it into `build.yml`, or "unblocking" it. Do not open an issue about it. Do not add it to a debt ledger.

**Opt-in only:** unless `HUMANS_INTEGRATION_TESTS=1`, the csproj sets `IsTestProject=false`, so `dotnet test Humans.slnx` (or the project itself) builds it but runs nothing; IDE runners that still launch it get every fact skip-marked and no Postgres fixture (`IntegrationTestGate` in `tests/Humans.Testing`). This holds on every machine, Peter's included. **Agents never set `HUMANS_INTEGRATION_TESTS`** — only Peter opts in (and the localization sweep's runner). Never mention, investigate, or count the suite's absence or skips.

The one subset that *can* run in CI is filtered **in** by name from its own workflow — `localization-sweep.yml` selects `~LocalizationCoverageSweep` on a cron. That single carve-out is not evidence the rest could run; it is the exception that already got its own job.

**Why:** The project's whole purpose is holding the tests CI can't host. Counting its tests as "excluded coverage" mistakes the container for a backlog. This has been raised and settled more than once; each round costs a real conversation.

**How to apply:**
- Nightly debt runs and manual runner trials exclude this suite even on Peter's machine. Use `FullyQualifiedName!~Humans.Integration.Tests`; preserve that exclusion in any narrower test filter. Local availability is not authorization to run it as part of this automation.
- Auditing coverage or CI: report `Humans.Integration.Tests` as out of scope by design. Never as a gap, a risk, or a number of "tests that run nowhere".
- Writing a test that must run in CI: put it in the section's own `tests/Humans.<Section>.Tests/` project. If it only works against a live external dependency, `Humans.Integration.Tests` is correct and **it will not run in CI — say so**, and don't call CI its gate (see the CI-reachability check in `.claude/skills/section-doctor/SKILL.md` Phase 4).
