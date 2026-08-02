# Mailer — G0 First Audit

**Section:** Mailer · **Kind:** vertical (MailerLite orchestration; owns no tables — subsumed under `Email` in `reforge.surface-score.json`) · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | N/A | Doc: "Mailer owns no tables. MailerLite is the system of record." No `MailerRepository` exists. |
| 2 | One writer-service per table | N/A | No owned tables; in-Humans writes are explicitly routed through other sections' services (`UserEmailService`, `AccountProvisioningService`, `CommunicationPreferenceService`, `UserService`) rather than direct writes. |
| 3 | No EF entity leaks across boundary | PASS | `MailerImportService`/`MailerAudienceSyncService` never touch EF; pinned by `MailerArchitectureTests` (no-EF assertion). |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | No Mailer rows in any baseline file. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | PASS | No navs (no entities), no `[Grandfathered]` hits, no baseline rows. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | No `Grandfathered` hits on Mailer controllers (`Web/Controllers/Mailer/**`). All routes gated `AdminOnly`. |
| 7 | `docs/sections/Mailer.md` current | PASS | Very precise — enumerates the exact allowed write surface on `IMailerLiteService` (4 methods) and the "Humans - " prefix write guard, both pinned by named architecture tests; matches code. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | N/A | No repository, no owned tables. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | PASS | Grep for `HumansDbContext` across `tests/Humans.Application.Tests/Services/Mailer/**`: no matches. `MailerImportService` cross-section reads are through interfaces (`IUserEmailService`, `IAccountProvisioningService`, `ICommunicationPreferenceService`, `IUserService`, `ITicketServiceRead`, `IShiftView`), consistent with the architecture test pinning "no cross-section repository injection in `MailerImportService`". |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively mapped, but this is the best-covered section in the batch by file count: `MailerImportServiceClassifierTests`, `ConflictRuleTests`, `IdempotencyTests`, `ThrottleTests`, `WebsiteScopeTests`, `MailerAudienceBaseTests`, per-audience tests (`HasShiftAudienceTests`, `HasTicketAudienceTests`, `MarketingAudienceTests`, etc.), `MailerLiteClientWriteGuardTests`, `MailerLiteClientRetryTests`, `MailerLiteClientCacheTests` each map to a named invariant in the doc. No line-level confirmation done, but naming correspondence is strong. |
| 4 | No skipped tests without an issue ref | PASS | No `Skip=` anywhere in `tests/`. |
| 5 | Tests grouped under section | PASS | `tests/Humans.Application.Tests/Services/Mailer/**` (+ `Audiences/` subfolder) and `tests/Humans.Web.Tests/Controllers/Mailer/**` — cleanly grouped. |

## G1 gap list

No G1 gaps found for Mailer.

## G2 queue notes

None — no owned tables.

## Verdict

`G1: met · G3: met (1 PARTIAL, not a real gap) — headline: cleanest and best-tested section in this batch; no action items`
