---
name: "`Humans.Integration.Tests` runs on PRs and nightly, and a red run is owned"
description: `Humans.Integration.Tests` is opt-in (`HUMANS_INTEGRATION_TESTS=1`) and stays out of a default `dotnet test`, but `integration-tests.yml` runs it on every code PR and nightly. A red run is work, never noise - fix the code or the test.
---

# `Humans.Integration.Tests` runs on PRs and nightly, and a red run is owned

**Rule:** `tests/Humans.Integration.Tests/` boots the whole app in-process against a throwaway Postgres container. It needs Docker and nothing deployed. `integration-tests.yml` runs it, split across parallel jobs, on every PR that touches code and once a night on main. When it is red, someone fixes it: the session that opened the PR, or for a nightly run the `nightly-red` issue the workflow opens. `e2e-qa.yml` (Playwright against QA) is nightly only, and opens the same kind of issue.

**Still opt-in locally:** unless `HUMANS_INTEGRATION_TESTS=1`, the csproj sets `IsTestProject=false`, so `dotnet test Humans.slnx` builds the project and runs nothing; `build.yml` filters it out as well. That keeps the default gate fast and Docker-free. It is not a statement that the suite does not matter.

**Why:** from 2026-09 to 2026-11 the rule here was the opposite: agents never ran or mentioned the suite, and no PR ran it. Every refactor left tests behind unseen, the nightly run was red for weeks with no owner, and 55 of 312 tests had drifted by the time anyone looked (Peter, 2026-11-12). None were flaky. A test nobody runs at the source of a change only records how far the app has moved.

**How to apply:**
- A red `integration (n/4)` check on your PR is yours, like any other red check. Reproduce it: start Docker, then `HUMANS_INTEGRATION_TESTS=1 dotnet test tests/Humans.Integration.Tests -v quiet --filter "FullyQualifiedName~<TestClass>"`.
- Set `HUMANS_INTEGRATION_TESTS=1` when you are fixing one of these tests, changing the suite, or working a `nightly-red` issue. Do not add it to the routine verification gate; CI runs the suite for you.
- Never skip, disable or weaken a test to get green. If the test found a real defect, fix the defect.
- Seed test data through the owning section's seeding verbs (`IEventSettingsSeeding`, `IShiftSeeding`, `ICampSeeding`), never by writing to its `DbContext`: sections cache their data, so a direct write is invisible to the app.
- A test that must gate a single section still belongs in `tests/Humans.<Section>.Tests/`; this project is for what needs the whole app.
- The split lives in `.github/scripts/integration-shard.py`. A new test class needs no registration.
