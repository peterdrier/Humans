# `Humans.Integration.Tests` is local-only — never propose running it in CI/cloud, never report it

**Rule:** `tests/Humans.Integration.Tests/` is the home of tests that run **only in Peter's local environment** — they integrate with things that exist nowhere else. `build.yml`'s `--filter "FullyQualifiedName!~Humans.Integration.Tests"` is the design, not an oversight, not a stale Docker workaround, and not tech debt. Never propose adding a CI job for the project, moving it into `build.yml`, or "unblocking" it. Do not open an issue about it. Do not add it to a debt ledger.

**Self-skip:** under CI (`CI` env var) or a cloud agent run (`CLAUDE_CODE_REMOTE`, `CODEX_CI`), every fact in the suite is skip-marked and its Postgres fixture never starts (`IntegrationTestGate` in `tests/Humans.Testing`). A full-solution `dotnet test Humans.slnx` there goes green with the suite reported as Skipped. **Skipped integration tests are the design working — never mention them, never investigate them, never count them.** `HUMANS_INTEGRATION_TESTS=1` forces the suite on (the localization sweep's runner sets it); agents never set it.

The one subset that *can* run in CI is filtered **in** by name from its own workflow — `localization-sweep.yml` selects `~LocalizationCoverageSweep` on a cron. That single carve-out is not evidence the rest could run; it is the exception that already got its own job.

**Why:** The project's whole purpose is holding the tests CI can't host. Counting its tests as "excluded coverage" mistakes the container for a backlog. This has been raised and settled more than once; each round costs a real conversation.

**How to apply:**
- Auditing coverage or CI: report `Humans.Integration.Tests` as out of scope by design. Never as a gap, a risk, or a number of "tests that run nowhere".
- Writing a test that must run in CI: put it in the section's own `tests/Humans.<Section>.Tests/` project. If it only works against a live external dependency, `Humans.Integration.Tests` is correct and **it will not run in CI — say so**, and don't call CI its gate (see the CI-reachability check in `.claude/skills/section-doctor/SKILL.md` Phase 4).
