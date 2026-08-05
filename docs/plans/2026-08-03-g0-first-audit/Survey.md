# Survey — G0 First Audit

**Section:** Survey · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

> Note: the section doc lives at `docs/sections/survey.md` (lowercase) — an outlier against the otherwise-PascalCase `docs/sections/*.md` convention. Flagged under G1.7 below.

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | `reforge ownership-violations --owner Survey --tables surveys,survey_questions,survey_question_options,survey_invitations,survey_responses,survey_answers` → **0 violations**. `ISurveyRepository` is `internal sealed`, tagged `[Section("Survey")]`. |
| 2 | One writer-service per table | PASS | Single `SurveyService`; no interceptor pattern. |
| 3 | No EF entity leaks across boundary | PASS | No `Survey*` rows in `ApplicationServiceEntityReadReturns.baseline.txt`. `SurveyService` implements `ISurveyService`, `ISurveyServiceRead`, `IUserDataContributor` — read-interface split already in place (`ISurveyServiceRead`: 0 methods, "boundary established" for future consumers). |
| 4 | No cross-section EF joins | PASS | No baseline entries; doc explicitly notes cross-domain refs are bare `Guid` FK columns with **no navigation properties and no cross-section EF FK constraints** ("the older Issue/Feedback/Camp nav-stitching debt is not propagated" — born clean per the doc's own framing). |
| 5 | No `[Obsolete]` navs / `[Grandfathered]` / owned baseline rows | PASS | None found. |
| 6 | Controllers thin (no HUM0031 grandfathers) | PASS | `SurveyController`, `SurveyAdminController`, `SurveysApiController` all absent from the HUM0031 grep hit list. |
| 7 | `docs/sections/survey.md` current | PASS content-wise, **filename-casing gap** | Content is thorough and current (anonymity-tier table, branching evaluator, idempotent invite ledger all match the code read this pass). The file is `survey.md` (lowercase) while every sibling section doc is PascalCase (`Onboarding.md`, `Profiles.md`, `Shifts.md`, …) — cosmetic but worth a rename for tooling/glob consistency (e.g. this very audit's `Glob docs/sections/*.md` pattern still caught it, but a case-sensitive tool might not). |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | **UNVERIFIED — no dedicated repo test file found** | No `SurveyRepositoryTests.cs` (or similarly named file) exists under `tests/Humans.Application.Tests/Repositories/`. Repository behavior appears to be exercised indirectly through `SurveyServiceTests.cs`/`SurveyWizardFlowTests.cs` rather than a dedicated repo-level test. This isn't an EF-InMemory violation (none found), but it does mean G3.1 has no direct evidence either way — flagging as a coverage gap rather than a pass. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | PASS | `SurveyServiceTests.cs` — zero hits for `UseInMemoryDatabase`/`UseNpgsql`/`HumansDbContext`/`ServiceTestHarness`. Cleanest service-test posture of any table-owning section in this batch (ties with Store). |
| 3 | Invariants/triggers each have a test | PASS (spot-check) | `SurveyServiceTests.cs` has direct hits for `Anonymity`/`CompletionTracked`/`Identified` — the section's single most safety-critical invariant (the anonymity-tier encoding table) has test presence. `SurveyBranchingEvaluatorTests.cs` and `SurveyInviteTokenTests.cs` cover the other two named pure-logic invariants (branching, tokenised links) as dedicated files — strong invariant→test correspondence for a section this size. |
| 4 | No skipped tests without issue ref | PASS (tentative) | No hits found. |
| 5 | Tests grouped under section | PASS | `tests/Humans.Application.Tests/Surveys/` (4 files) + `tests/Humans.Web.Tests/Models/Survey/` (3 files) + `tests/Humans.Application.Tests/Architecture/SurveyArchitectureTests.cs` + `tests/Humans.Application.Tests/Controllers/SurveysApiControllerTests.cs` + `tests/Humans.Integration.Tests/Controllers/SurveyAdminControllerTests.cs` — well-scoped, if split across three test projects (Application/Web/Integration) as expected for a full-stack section. |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| `docs/sections/survey.md` filename is lowercase, breaking the PascalCase sibling convention | `docs/sections/survey.md` → **`docs/sections/Surveys.md`** (corrected 2026-08-03) | Rename for consistency. Target the **plural** form: the confirmed section inventory renames this canonical section `Survey` → `Surveys`, and its follow-up list calls for the doc to become `Surveys.md`. Stopping at singular `Survey.md` would land the file out of alignment with the canonical tracker name the moment it's renamed. Update inbound links (most reference it by section name, not literal path, so risk is low). | y |

## G3 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| No dedicated `SurveyRepositoryTests.cs` | `tests/Humans.Application.Tests/Repositories/` | Add a repository-level test file (real-Postgres-fixture, per G3.1) covering the six `survey_*` tables directly, rather than relying solely on service-level indirection. Lower priority than sections with active EF-InMemory violations, since there's no InMemory usage to migrate away from — this is a coverage gap, not a technical-debt migration. | y |

## Schema demolition queue

Survey is "born §15-compliant" (2026-06-04) — no demolition-inventory items identified in the doc or this audit.

## Headline

Best G1/G3 posture of the sections audited in this pass.
