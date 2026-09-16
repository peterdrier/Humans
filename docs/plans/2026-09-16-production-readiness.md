# Production readiness after the September Board meeting

Owner: Peter. Implementation coordination: Codex with independent fix and review agents.

## Objective and authority

Make the next production promotion an improvement in functionality, reliability and stability. Peter authorized preserving the review, fixing its findings, and opening focused PRs to `peterdrier/Humans` on 2026-09-16. Peter merges. Promotion to `nobodies-collective/Humans` remains a separate explicit decision after the Board meeting.

This is the durable release ledger. Update each row with its disposition, regression evidence and PR link; do not rely on a local worktree report or conversation memory.

## Reviewed snapshot

- Production: `1682857e33c27cb22b43790e21a9a035d5c6b477`.
- Fork candidate: `f1298b228d9ad0268b46ea2ed0dc9de1196d7181`.
- Review covered the endpoint delta, including Workgroups, assembly votes, Rideshare, Calendar all-day changes and section refactors. Behavioral source review was broader than assertion-level test or translation review.
- Recommendation at review completion: hold this candidate for the access-revocation and core workflow fixes below.

## Findings and acceptance criteria

| ID | Priority / owner | Trigger and required behavior | Evidence at review / status |
| --- | --- | --- | --- |
| R01 | P1 / GoogleIntegration | Workgroup folder permission combines inherited Viewer and direct writer. Dormancy must reduce direct elevation; leaving/retirement must remove the managed elevation while preserving inherited access. `GoogleDriveAccessSyncService.BuildPlan` currently excludes the mixed permission from downgrade/removal. | Candidate probe: dormant classifies Inherited; departure produces no plan rows. In implementation. |
| R02 | P2 / Users + Web | Named suspended/rejected/deletion-pending users must retain their documented self-service email access after ProfileEmails extraction. Permitted basic profile viewing must survive ProfileView extraction. Preserve other account-state gates. | Actual global-filter/MVC probe reproduces redirect regression. In implementation. |
| R03 | P2 / Calendar | Change only occurrence five's title in a weekly COUNT=5 series, then shorten to COUNT=2. Only valid remaining identities and intentionally moved exceptions should appear. | Actual candidate expander returns September 1, 8 and 29. Baseline required a moved start for orphan injection. In implementation. |
| R04 | P2 / Camps | A lead assigned in a prior season must retain access to a pending renewal when cached years omit the assignment. Cache hits and misses must provide equivalent authorization facts. | Source trace through warm-year projection, slug cache, renewal and new privacy gate. Existing controller fixture bypasses cache. In implementation. |
| R05 | P2 / Workgroups | A future scheduled meeting must not grant credit for activity that has not happened and suppress overdue update/dormancy actions. | Source trace produces negative silence for future meetings. In implementation. |
| R06 | P2 / Base + Users | Shared Human avatar URLs must resolve to the moved ProfileView.Picture action. | MVC discovery finds no old Profile.Picture action. In implementation. |
| R07 | P2 / Scanner | Lookup A starts, B succeeds, A fails later. B's display must remain. Every asynchronous completion must apply only to the current lookup. | Actual baseline/candidate JS reproduces new stale-error overwrite. In implementation. |
| R08 | P2 / Rideshare | Server-rendered list interest controls must work when MapLibre construction, map loading or GeoJSON fetch fails. | Actual JS probe: handlers attach on success, none after either failure. In implementation. |
| R09 | P2 / Workgroups | Settings and Register existing group must remain discoverable to authorized admins when the review queue is empty. | Tile returns null; no alternate navigation contribution. In implementation. |
| R10 | P2 / Workgroups + Surveys | Reject excessive ordinary form input before persistence: Workgroup log body 16,000; lifecycle reasons 4,000; survey rejection note 4,000. Explain validation and preserve input. | UI/service/schema trace; PostgreSQL invalid-input fixture not run during review. In implementation. |
| R11 | P2 / Surveys | Submit and ranked recalculation controls must reflect the actual resource authorization for their viewer. Server denials remain enforced. | Board sees Submit on others' drafts; authors see Board-only recalculation. Both return 403. In implementation. |
| R12 | P2 / Workgroups | Meeting LocationUrl field, model and schema limits must agree and server errors must be visible. | Field/schema permit 2,000, model 500, no rendered property error. In implementation. |
| R13 | Policy/copy / Workgroups | Private-meeting wording must match the register's actual audience. Approved design allows approved humans to read register meetings/minutes; UI says member-only. IsPublic gates community-calendar listing. | Contradictory promise, not an established authorization bypass. Peter's disposition requested. |

## Work loop

1. Reverify the finding against current code and prior maintainer decisions.
2. Reproduce the invariant with a meaningful regression test; exercise the actual filter, cache, expander or browser module where the defect lives.
3. Fix at the source using existing section-owned surface. No architecture bypasses, runtime-state edits or speculative adjacent features.
4. Verify applicable negative authorization, audit, GDPR, cultures, invariant docs, migrations and navigation. Explain non-applicable surfaces in the PR.
5. Independently review the final diff, then publish a focused PR against fork main. Every lane branches from `origin/main`, with its own worktree; no stacked lane dependencies.
6. Triage actual CI/review events under `.claude/skills/steward/SKILL.md`; preserve dispositions and the review-round ceiling. Peter merges.
7. Record final combined verification and residual release decisions here before production promotion.

Parallel ownership: GoogleIntegration permissions; Users/Web/Base/Camps/Calendar; Workgroups/Surveys; Scanner/Rideshare and release coordination. Compiler use is serialized across lanes.

## Existing verification and remaining release gates

- Full solution compiled. Final full test run passed. Four initial Web failures passed targeted and whole-project reruns, then the final solution run; their initial cause was not established.
- PostgreSQL 16 rehearsal applied baseline schemas then candidate migrations for Budget, Calendar, Email, Governance, Rideshare, Surveys and Workgroups. All upgrades and pending-model checks passed.
- That database held **empty baseline schemas**, not production data. A production-shaped rehearsal remains a release gate.
- Focused executable probes confirmed R01/R02/R03/R06/R07/R08. Other findings were source-verified. Live Google writes and authenticated browser journeys were not exercised during review.
- Resource parity passed; translation quality and all rendered cultures were not manually verified.
- After fixes: focused preview journeys for affected roles/actions; combined build/tests; review any later changes to fork main; production-shaped migration rehearsal and explicit rollback/recovery preparation.
- Calendar DATE-based all-day writes and Email's approved dropped ShiftSignupId column prevent assuming old binaries alone provide rollback. Review backup/recovery sequencing.
- PR `peterdrier/Humans#1680` intentionally provides an audited Admin repair for existing term expiries. Review its intended use before assembly electorates rely on stored expiry values. No manual database repair.
- No production promotion until Peter explicitly authorizes it.

## Prior dispositions that remain binding

Do not reopen accepted behavior without new evidence: Holded numberless-row failure; Gate enqueue/ledger ordering; concurrent-signup losing-request error; Rideshare previously declined lookup/cache, fallback geometry and notification choices; Survey export identifiers/timestamps; assembly merged-account recipient policy; Guide's retained parser debt. Existing vendor-resync PII and Holded job-visibility debt were not introduced by this candidate.

Earlier reviewed PR context includes `peterdrier/Humans#1575`, `peterdrier/Humans#1579`, `peterdrier/Humans#1603`, `peterdrier/Humans#1608`, `peterdrier/Humans#1618`, `peterdrier/Humans#1624`, `peterdrier/Humans#1647`, `peterdrier/Humans#1650`, `peterdrier/Humans#1655`, `peterdrier/Humans#1658`, `peterdrier/Humans#1659`, `peterdrier/Humans#1680` and `peterdrier/Humans#1699`.
