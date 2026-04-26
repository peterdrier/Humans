# Maintenance Log

Tracks when recurring maintenance processes were last run.

| Process | Last Run | Next Due | Cadence | Est. Cost | Notes |
|---------|----------|----------|---------|-----------|-------|
| NuGet vulnerability check | 2026-04-05 | 2026-04-12 | Weekly | — | `dotnet list package --vulnerable` |
| Freshness sweep (diff) | — | — | Daily | — | `/freshness-sweep` — auto-refresh drift-prone docs against upstream/main diffs. First run pending. |
| Freshness sweep (full) | — | — | Weekly | — | `/freshness-sweep --full` — full regeneration of every catalog entry. First run pending. |
| Todo audit | 2026-03-08 | 2026-03-15 | Weekly | — | Stale items, completed moves |
| Code simplification | 2026-02-24 | — | After features | codex: ~5% | Dead code, unused abstractions |
| ReSharper InspectCode | 2026-04-05 | 2026-04-12 | Weekly | — | `/resharper` — fix Tier 1+2 warnings. Codex can't run `jb` in sandbox. |
| Context cleanup | 2026-03-18 | 2026-04-18 | Monthly | — | CLAUDE.md, .claude/, todos.md |
| Feature spec sync | 2026-04-05 | 2026-05-05 | Monthly | — | docs/features/ vs implementation |
| i18n audit | 2026-02-24 | 2026-03-24 | Monthly | gemini: ~2% | Missing translations |
| Data model doc sync | 2026-02-12 | As needed | As needed | — | docs/architecture/data-model.md vs entities |
| Navigation audit | 2026-03-22 | 2026-04-22 | Monthly | — | `/nav-audit` — discoverability, backlinks |
| GDPR audit | — | — | Quarterly | — | Exports, consent, PII logging |
| Migration squash check | 2026-02-24 | 2026-03-24 | Monthly | — | Check `/Admin/DbVersion` on prod, QA (humans.n.burn.camp), and local dev. Oldest `lastApplied` across all three is the safe squash boundary. |
| NuGet full update | 2026-04-25 | 2026-05-25 | Monthly | — | Non-security package updates |
| About page package sync | 2026-04-25 | 2026-05-25 | Monthly | — | Update `About.cshtml` package versions after NuGet updates |
| GitHub issue triage | 2026-03-08 | 2026-03-15 | Weekly | — | Sync issues vs todos.md |
| Access matrix verification | 2026-03-18 | 2026-03-25 | Weekly | — | Compare `AccessMatrixDefinitions.cs` against actual controller auth checks |
| Service ownership migration | 2026-04-15 | As needed | Per-section | — | Governance landed as first full end-to-end spike in PR #503. Profile is §15a Step 0 next. |
| User guide created | 2026-04-20 | — | One-time | — | `docs/guide/` with 14 section guides + README, GettingStarted, Glossary. Plan: `docs/superpowers/plans/2026-04-20-user-guide.md`. |
| Screenshot review | 2026-04-20 | 2026-05-20 | Monthly | — | Review outstanding `TODO: screenshot` placeholders in `docs/guide/` and spot-check existing screenshots against the live UI at `nuc.home`. Process: `docs/architecture/screenshot-maintenance.md`. |
| Community calendar slice 1 | 2026-04-21 | — | One-time | — | New entities `CalendarEvent`, `CalendarEventException`. Added `Ical.Net` 5.2.1 (MIT) for RFC 5545 RRULE expansion. Plan: `docs/superpowers/plans/2026-04-21-community-calendar-slice1.md`. |
| xUnit v2 → v3 upgrade | 2026-04-24 | — | One-time | — | Bumped `xunit` 2.9.3 → `xunit.v3` 3.2.2 (keeps `xunit.runner.visualstudio` 3.1.5). Added `tests/xunit.runner.json` with `longRunningTestSeconds: 1`, `--blame-hang-timeout 2m` on CI, and a skipped `GlobalTimeoutDemoTest` to prove per-test `[Fact(Timeout = N)]`. Suppressed `xUnit1051` (CancellationToken-threading advisory; 1700+ pre-existing call sites — follow-up cleanup). See nobodies-collective/Humans#586. |
| HumansFact / HumansTheory introduced | 2026-04-25 | — | One-time | — | Project-wide ban on bare `[Fact]` / `[Theory]` via `BannedApiAnalyzers` (RS0030) + replacement attributes in `tests/Humans.Testing/` (5s default Timeout, infinite forbidden via setter validation). ReSharper sweep + per-test Timeout overrides on ~30 slow tests. CI grep step `Forbid RS0030 suppressions in test code` blocks pragma escape hatches. Default Timeout will be lowered over time as slow bits are cleaned up. |

- **2026-04-25** — Completed nobodies-collective/Humans#580 (HumansMetricsService inversion). Each section now owns its metrics. IHumansMetrics + HumansMetricsService deleted.
