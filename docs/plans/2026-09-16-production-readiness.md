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
| R01 | P1 / GoogleIntegration | Workgroup folder permission combines inherited Viewer and direct writer. Dormancy must reduce direct elevation; leaving/retirement must remove the managed elevation while preserving inherited access. | Fixed in [peterdrier/Humans#1714](https://github.com/peterdrier/Humans/pull/1714). Section gate: 282 passed. Real SDK wire/error tests use inert transport; no live Google permission writes. |
| R02 | P2 / Users + Web | Named suspended/rejected/deletion-pending users must retain their own profile/email self-service after extraction. Messaging, administrative actions and enriched views of others still require an Active actor. | Fix in [peterdrier/Humans#1712](https://github.com/peterdrier/Humans/pull/1712); review round narrows exemptions to explicit self-service actions. Original suggestion to exempt all ProfileView actions was overbroad: those reads include role-sensitive data. Basic visibility of suspended **targets** remains intentional. |
| R03 | P2 / Calendar | Change only occurrence five's title in a weekly COUNT=5 series, then shorten to COUNT=2. Only valid remaining identities and explicit temporal exceptions should appear. | Timed/date recurrence and end-only extension regressions pass; fix in [peterdrier/Humans#1712](https://github.com/peterdrier/Humans/pull/1712). |
| R04 | P2 / Camps | A lead assigned in a prior season must retain access to a pending renewal when cached years omit the assignment. Cache hits and misses must provide equivalent authorization facts. | Real repository/service/cache/authorization regression now passes; fix in [peterdrier/Humans#1712](https://github.com/peterdrier/Humans/pull/1712). |
| R05 | P2 / Workgroups | A future scheduled meeting must not grant credit for activity that has not happened and suppress overdue update/dormancy actions. | Source trace produces negative silence for future meetings. In implementation. |
| R06 | P2 / Base + Users | Shared Human avatar URLs must resolve to the moved ProfileView.Picture action. | Real MVC URL-generation regression now passes; fix in [peterdrier/Humans#1712](https://github.com/peterdrier/Humans/pull/1712). |
| R07 | P2 / Scanner | Lookup A starts, B succeeds, A fails later. B's display must remain. Every asynchronous completion must apply only to the current lookup. | Baseline failure and deployed fix verified in Chromium and module regressions. Fix and CI wiring in [peterdrier/Humans#1713](https://github.com/peterdrier/Humans/pull/1713), ready for review. |
| R08 | P2 / Rideshare | Server-rendered list interest controls must work when MapLibre construction, map loading or GeoJSON fetch fails. | Baseline failure and deployed fix verified in Chromium and module regressions. Fix and CI wiring in [peterdrier/Humans#1713](https://github.com/peterdrier/Humans/pull/1713), ready for review. |
| R09 | P2 / Workgroups | Settings and Register existing group must remain discoverable to authorized admins when the review queue is empty. | Tile returns null; no alternate navigation contribution. In implementation. |
| R10 | P2 / Workgroups + Surveys | Reject excessive ordinary form input before persistence: Workgroup log body 16,000; lifecycle reasons 4,000; survey rejection note 4,000. Explain validation and preserve input. | UI/service/schema trace; PostgreSQL invalid-input fixture not run during review. In implementation. |
| R11 | P2 / Surveys | Submit and ranked recalculation controls must reflect the actual resource authorization for their viewer. Server denials remain enforced. | Board sees Submit on others' drafts; authors see Board-only recalculation. Both return 403. In implementation. |
| R12 | P2 / Workgroups | Meeting LocationUrl field, model and schema limits must agree and server errors must be visible. | Field/schema permit 2,000, model 500, no rendered property error. In implementation. |
| R13 | Policy/copy / Workgroups | Private-meeting wording must match the register's actual audience. Approved design allows approved humans to read register meetings/minutes; UI says member-only. IsPublic gates community-calendar listing. | Retain the approved broad register policy; clarify calendar-listing wording in all cultures. No authorization change. In implementation. |
| R14 | P2 / Shifts | A non-Active actor can reach signup through exempt onboarding/dietary endpoints. Enforce actor eligibility in the owning signup service for single and range signup; preserve named Active pre-consent onboarding and explicit on-behalf workflows. | Found while reviewing retained self-service actions. Both callers reach `ShiftSignupService` without a state check; Public rotas auto-confirm. Separate fix lane from fork main. |

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

### Promotion rehearsal and recovery record

Complete this against the final merged fork SHA before opening the production promotion:

1. Restore a recent production backup into an isolated rehearsal database, using a PostgreSQL client compatible with the production server. Record the actual server/client versions, backup timestamp and source release; QA's PostgreSQL 16 rehearsal is not proof for the production server.
2. Keep outbound integrations in their supported stub/disabled configuration. Start the candidate normally so its section migrations run through the same path as deployment. Record migration histories, relevant before/after row counts, startup logs and `/api/version`.
3. Check existing Calendar all-day dates/recurrences and Email history across migration, plus newly introduced Governance/Workgroups data paths. Verify the supported term-expiry repair's intended use before freezing any assembly electorate.
4. Restore the pre-upgrade backup into a second scratch database and boot the previous production image. Record a healthy boot and matching migration histories/data checks. Image rollback alone is insufficient after incompatible schema/data changes.
5. Confirm the deployment snapshot location persists across container replacement and that the known-good image, database backup and uploads backup are available. Use the [database restore runbook](../database-restore-runbook.md), checking its commands against the current section catalog and actual server version.

These are pending release gates, not completed evidence. No production database was copied or mutated during this fix run.

## Prior dispositions that remain binding

Do not reopen accepted behavior without new evidence: Holded numberless-row failure; Gate enqueue/ledger ordering; concurrent-signup losing-request error; Rideshare previously declined lookup/cache, fallback geometry and notification choices; Survey export identifiers/timestamps; assembly merged-account recipient policy; Guide's retained parser debt. Existing vendor-resync PII and Holded job-visibility debt were not introduced by this candidate.

Earlier reviewed PR context includes `peterdrier/Humans#1575`, `peterdrier/Humans#1579`, `peterdrier/Humans#1603`, `peterdrier/Humans#1608`, `peterdrier/Humans#1618`, `peterdrier/Humans#1624`, `peterdrier/Humans#1647`, `peterdrier/Humans#1650`, `peterdrier/Humans#1655`, `peterdrier/Humans#1658`, `peterdrier/Humans#1659`, `peterdrier/Humans#1680` and `peterdrier/Humans#1699`.

## Maintainer decisions (2026-09-16)

- R01: Peter approved `UpdatePermissionAsync(fileId, permissionId, role, ct)` on the existing internal `IGoogleDrivePermissionsClient`. Existing create/delete operations cannot safely reduce mixed inherited/direct permissions. Regression tests reproduce the defect. No live Google writes.
- R05: Peter approved appending `AuditAction.WorkgroupDormancyCleared` and any needed labels. The daily job needs an accurate immutable audit record when a meeting reaches its scheduled time and clears the inquiry; editable Workgroup notes cannot substitute.
- R13: Peter chose broad register access. The checkbox controls community-calendar listing; meeting and minutes access stays unchanged. Updated copy covers all six cultures.

## Fix verification updates

- peterdrier/Humans#1712 passed full solution build/tests with serial project scheduling. Independent review caught and corrected a valid end-only Calendar extension regression before publication. No schema/interface additions.
- Scanner/Rideshare Node regressions pass and independent review accepted their source changes. Authenticated Chromium reproduced baseline failures on preview1711, then verified the actual deployed scripts on preview1713 at `ecf13d2e` with controlled network/map failures. Rideshare fixtures were created through ordinary forms on isolated previews; no database edits.
- Scanner/Rideshare full build passed; the first solution test run had timeout-only failures, and an unchanged full retry passed. No timeout changes or test exclusions. PR1713 is ready for review at `3cbdfbb96bd67465c96a8a5c63b9d27a9217e6ae`, including the Node 24 CI step. GitHub accepted the workflow push after the token gained `workflow` scope; local regressions passed again with the exact CI command, `node --test 'tests/Humans.*.Tests/Browser/*.test.*'`.
- Cross-review found that new Workgroups/Surveys bounds rejected invalid input correctly but two admin redirects discarded the submitted text. Those failure paths are being corrected to preserve it.
