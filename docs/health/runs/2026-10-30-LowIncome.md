# section-doctor — LowIncome — 2026-10-30

- Invocation: unattended (scheduled routine section-doctor-daily, prompt: Run the section-doctor skill unattended.)
- Anchor commit: `71037de84` (origin/main at branch point); branch `section-doctor/2026-10-30T003937Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#2216

## Assessment summary

- 2026-10-30 session: first doctor run on a section that landed with Grants (BASE `0bf83d00e^`). SDK, `dotnet-ef` and reforge present; reforge surface-score ran after a full build. Open PRs and issues were listed through the REST API because `gh pr list` (GraphQL) and issue search are blocked here; nobodies-collective/Humans is not readable from this session, so the Inbox covered peterdrier/Humans only.
- 2026-10-30 session: no live render. The one view changed nothing; the preview deploy is where a render is checked.
- Selector: LowIncome, the only unrun section past its cool-down; AuditLog and Debug were blocked by an open doctor branch.
- Target diff: no earlier target; this run writes the first.
- Reading threads ran on main: the section is a controller, a view and its docs, so dispatching a reader per lens would cost more than reading it.
- Independence check: pass. The unreachable refusal arm (finding 1) is a shape mismatch against the target's one pipeline; the Board-or-Admin drift (finding 2) and the repeat-grant message (finding 4) are spec-versus-code deltas from reading Grants' controller and contract.

## Findings

1. `LowIncomeAdminController.RefusalMessage` mapped `GrantWriteRefusal.SourceReferenceTooLong` to a message, but both calls pass `sourceRef: null`, so Grants can never return that refusal here.
2. `LowIncome.md` said an Admin creates the Low Income program; `GrantsAdminController` lets Board or Admin create programs (`PolicyNames.BoardOrAdmin` on New).
3. `LowIncome.md`'s third invariant packed three claims into internal jargon ("typed source-write refusals preserve the operator guidance").
4. Giving the grant to someone who already holds it reports "Low Income grant given" though Grants changed nothing and told nobody: `IGrantWrite.IssueAsync` is idempotent and `GrantIssueResult` cannot say the grant already existed.
5. `memory/code/authorization-conventions.md` places `PolicyNames.cs` under `src/Humans.Web/Authorization`; it lives in `src/Humans.Base/Authorization`.
6. The section had no `health.md` target.
7. reforge reports the two constructor dependencies (`IGrantWrite`, `IUserService`) as cross-section reads; both are the section's purpose or come with `HumansControllerBase`, so neither is debt.

## Debt verified

- The section has no `Docs/debt.yml`, and no central ledger row names LowIncome; nothing to verify. The cap did not bind.

## Worked

- 1 — arm cut; the default arm still throws for any refusal the page cannot produce. Section tests green.
- 2, 3 — `LowIncome.md` corrected and the invariant split into two plain statements.
- 4 — not ledgered: Peter ruled the placeholder fine as it is (CENTRAL-103 dropped).
- 5 — ledgered as CENTRAL-104.
- 6 — `health.md` written with cites; trace gate clean apart from `ILowIncomeService`, which is named only under deliberately-not-done.

## Skipped

- Sections passed over as blocked: AuditLog and Debug (open branch `section-doctor/2026-10-29T003835Z`).
- 7 — not debt, see Findings.
- Inbox: no open peterdrier/Humans issue is about LowIncome; the Grants design issue (peterdrier/Humans#2099) lists Low Income as a source and belongs to Grants' review, so no issue comment was posted.

## Retro

- **Selector.** Right pick for the rotation, but a section this small finishes its whole cycle in well under the budget; nothing was wasted beyond the fixed per-run bookkeeping.
- **Wasted motion.** Reforge refused to score until a full solution build had run, which cost a few minutes of waiting; the build was needed for the gates anyway.
- **What striking revealed.** Nothing new; every strike was visible in the first read.
- **Target diff.** First target, nothing to diff.

## Needs Peter

- none

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.LowIncome/Controllers/LowIncomeAdminController.cs` | reviewed, changed |
| `src/Sections/Humans.LowIncome/Docs/LowIncome.md` | reviewed, changed |
| `src/Sections/Humans.LowIncome/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.LowIncome/Docs/health.md` | changed (new) |
| `src/Sections/Humans.LowIncome/Humans.LowIncome.csproj` | reviewed |
| `src/Sections/Humans.LowIncome/Section.cs` | reviewed |
| `src/Sections/Humans.LowIncome/Views/LowIncomeAdmin/Index.cshtml` | reviewed |
| `src/Sections/Humans.LowIncome/Views/_ViewImports.cshtml` | reviewed |
| `tests/Humans.LowIncome.Tests/Humans.LowIncome.Tests.csproj` | reviewed |
| `tests/Humans.LowIncome.Tests/LowIncomeAdminControllerTests.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | main session | 1, 6, 7 |
| Behavior & bugs | main | main session | 4 |
| Controller intent | self-run on main (section too small to dispatch) | main session | none: both POSTs translate and forward |
| Freshness | self-run on main | main session | 2, 5 |
| Conformance | detectors on main, classified on main | main session | none: no detector hit names LowIncome |
| Tests | self-run on main | main session | none: every invariant in the target has a test or a cited enforcement site |
| Prose & surface | reforge on main, classified on main | main session | 3, 7 |
| History | self-run on main (git log of the section) | main session | none |
| Comments | self-run on main | main session | none: every comment says why or warns |
| Inbox | REST issue list on main | main session | none for this section |

