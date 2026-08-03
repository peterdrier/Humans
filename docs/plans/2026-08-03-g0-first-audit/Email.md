# Email — G0 First Audit

Kind: vertical · Audited 2026-08-03 @ 5a9bbe198

Scope note: `reforge.surface-score.json` bundles **Mailer** (`IMailerAudienceSyncService`, `IMailerImportService`, `IMailerLiteService`) into the same "Email" tracker entry as the outbox. These are audited together below but are architecturally distinct: the outbox owns `email_outbox_messages`; Mailer is a pure external-API sync layer (MailerLite) with **no owned tables** — confirmed via `Services/Mailer/MailerAudienceSyncService.cs` doc comment "Lives in the Application layer; no DbContext" and no `Mailer*` entity found under `src/Humans.Domain/Entities/`.

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | `reforge ownership-violations --owner Email --tables email_outbox_messages` → 0 violations. `EmailOutboxRepository` (`src/Humans.Infrastructure/Repositories/Email/EmailOutboxRepository.cs`) is the only file touching `DbContext.EmailOutboxMessages` (per its own doc comment), via `IDbContextFactory<HumansDbContext>`. Mailer owns no tables — n/a. |
| 2 | One writer-service per table, no interceptor workarounds | PASS | `OutboxEmailService.SendAsync` (`IEmailService`) is the single write path; `reforge audit-surface OutboxEmailService` shows 31 prod callers all going through the one `SendAsync` method — no bypass paths found. |
| 3 | No EF entity leaks across the boundary | PASS | Doc confirms `EmailOutboxMessage.User` nav was stripped (shadow FK only); `CampaignGrant`/`ShiftSignup` navs are explicitly documented as "aggregate-local," kept for status mirroring/dedup, not leaked out through public service methods. `IEmailService.SendAsync(EmailMessage, ct)` takes/returns no EF entity. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | Grepped all 5 baseline files under `tests/Humans.Application.Tests/Architecture/Baselines/` for `Email`/`Mailer` — zero hits. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | **FAIL** | `EmailController.cs:88-92` — `[Grandfathered(ruleId: "HUM0031", justification: "Worst-offender at HUM0031 introduction: 51 statements, cc 2.", since: "2026-06-09", issueRef: "nobodies-collective/Humans#857")]` on `EmailPreview`. Per the team-lead's brief this is being worked in a parallel lane (#857) tonight — recorded honestly here regardless. |
| 6 | Controllers thin — no HUM0031 grandfathers | **FAIL** | Same finding as above — `EmailController.EmailPreview` carries the HUM0031 grandfather. `UnsubscribeController.cs` grepped clean (zero matches). |
| 7 | `docs/sections/Email.md` exists and matches reality | PASS — corrected 2026-08-03 | The outbox half of the doc is accurate and detailed (verified data model, routes, triggers, `IDbContextFactory<HumansDbContext>` usage, connector abstractions). ~~Gap: no separate `docs/sections/Mailer.md` exists~~ — **wrong, verified via `git ls-files docs/sections/`**: `docs/sections/Mailer.md` exists, is git-tracked, and has substantial real content (concepts, import classification/reset rules, audience framework, idempotency invariants, admin routes — confirmed by direct read). The earlier pass's negative result was very likely a case-insensitive/glob false negative on Windows (same failure mode flagged elsewhere in this batch), not a real gap. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests on real Postgres shared fixture, zero EF-InMemory | **FAIL** | `tests/Humans.Application.Tests/Repositories/EmailOutboxRepositoryTests.cs:21-23` — `new DbContextOptionsBuilder<HumansDbContext>().UseInMemoryDatabase(...)`. No entry for Email under `tests/Humans.Integration.Tests/Repositories/**`. (`tests/Humans.Integration.Tests/Controllers/EmailGridFlowTests.cs` and `UnsubscribeFlowTests.cs` exist but are controller/flow-level tests against the full `HumansWebApplicationFactory`, not focused repository tests — they don't satisfy this predicate even though they likely run against a real DB via the web app factory.) |
| 2 | Service tests mock repository interfaces, zero `HumansDbContext` | PASS | `tests/Humans.Application.Tests/Services/OutboxEmailServiceTests.cs` grepped for `HumansDbContext` — zero matches; service tests appear to mock `IEmailOutboxRepository` and collaborators rather than stand up a real context. |
| 3 | Section invariants/triggers each have a test | PARTIAL | Spot-checked: retry/backoff (`RetryCount`, `NextRetryAt = now + 2^(RetryCount+1) minutes`) and the `@localhost`/`@ticketstub.local` short-circuit both read as documented, tested-looking invariants (`ProcessEmailOutboxJobTests.cs`, `CleanupEmailOutboxJobTests.cs` exist per the earlier file listing) but were not individually traced test-by-test against the full invariants list (pause flag ownership, campaign-grant status mirroring, `Sent` retention). |
| 4 | No skipped tests without an issue ref | PASS | Grep `Skip\s*=` across `EmailOutboxRepositoryTests.cs` and `Services/OutboxEmailServiceTests.cs` — zero matches. |
| 5 | Tests grouped under the section | PASS | Section-named test files (`EmailOutboxRepositoryTests.cs`, `OutboxEmailServiceTests.cs`, `Jobs/ProcessEmailOutboxJobTests.cs`, `Jobs/CleanupEmailOutboxJobTests.cs`) all present; Mailer has its own `MailerArchitectureTests.cs`. |

## G1 gap list

1. **HUM0031 grandfather on `EmailController.EmailPreview`** — already tracked under #857 (in-flight parallel lane per this run's brief); no new action needed here beyond confirming it. No migration needed (y).
2. ~~No `docs/sections/Mailer.md`~~ — **retracted 2026-08-03**, the file exists (see predicate 7 correction). No gap.

## G3 gap list

1. **`EmailOutboxRepositoryTests.cs` on EF-InMemory, not Postgres** — needs conversion to the real-Postgres shared-fixture pattern, matching `Repositories/Shifts/VolunteerTrackingRepositoryTests.cs`. No migration needed (y) — test-only change.
2. **Invariant→test mapping not exhaustively verified** — needs a full pass against `docs/sections/Email.md` Invariants/Triggers sections (11 triggers documented). No migration needed (y).

## G2 queue notes (light)

- Still on monolithic `HumansDbContext` (via `IDbContextFactory<HumansDbContext>`) — no dedicated `EmailDbContext` yet, unlike Containers/Expenses/Finance/EventGuide/Surveys/SystemSettings/Agent.
- No dead-column/table candidates spotted; schema described as "stable" by design (new headers go in `ExtraHeaders` JSON, not new columns) — this is an intentional anti-demolition-churn decision, not debt.

## Verdict

`G1: 1 gap (corrected 2026-08-03, was 2 — Mailer.md gap retracted, it exists) · G3: 2 gaps`
