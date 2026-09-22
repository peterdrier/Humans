# Production readiness after the September Board meeting

Owner: Peter. Implementation coordination: Codex with independent fix and review agents.

## Objective and authority

Make the next production promotion an improvement in functionality, reliability and stability. Peter authorized preserving the review, fixing its findings, and opening focused PRs to `peterdrier/Humans` on 2026-09-16. Peter merges. Promotion to `nobodies-collective/Humans` remains a separate explicit decision after the Board meeting.

This is the durable release ledger. All tracked items are addressed in the linked PRs and recovery documentation. The application PRs have passing CI and resolved review findings. Peter's merges and the production rehearsal below remain release gates.

## Reviewed snapshot

- Production: `1682857e33c27cb22b43790e21a9a035d5c6b477`.
- Fork candidate: `f1298b228d9ad0268b46ea2ed0dc9de1196d7181`.
- Endpoint delta: 1,957 changed files, approximately 79,881 additions and 19,943 deletions. Independent streams covered People/access, money/integrations, Governance/Surveys/Workgroups, and Shell/release edges.
- Review covered the endpoint delta, including Workgroups, assembly votes, Rideshare, Calendar all-day changes and section refactors. Behavioral source review was broader than assertion-level test or translation review.
- Recommendation at review completion: hold this candidate for the access-revocation and core workflow fixes below.

## Findings and acceptance criteria

| ID | Priority / owner | Trigger and required behavior | Evidence at review / status |
| --- | --- | --- | --- |
| R01 | P1 / GoogleIntegration | Workgroup folder permission combines inherited Viewer and direct writer. Dormancy must reduce direct elevation; leaving/retirement must remove the managed elevation while preserving inherited access. | Fixed in [peterdrier/Humans#1714](https://github.com/peterdrier/Humans/pull/1714). Section gate: 282 passed. Real SDK wire/error tests use inert transport; no live Google permission writes. |
| R02 | P2 / Users + Web | Named suspended/rejected/deletion-pending users must retain their own profile/email self-service after extraction. Messaging, administrative actions and enriched views of others still require an Active actor. Deleted/Merged sessions must not re-enter recovery. | Fixed in [peterdrier/Humans#1712](https://github.com/peterdrier/Humans/pull/1712). Explicit self-service allowlists retain recoverable-state access; terminal states are checked before those exemptions. Composed name/membership-filter regressions pass. Basic visibility of suspended **targets** remains intentional. |
| R03 | P2 / Calendar | Shortening or changing recurrence must remove obsolete identities after text/end edits. Valid end extensions remain visible in later windows; genuinely moved starts/dates remain independent. | Fixed in [peterdrier/Humans#1712](https://github.com/peterdrier/Humans/pull/1712). Timed/date COUNT, UNTIL and weekday changes, unchanged starts, long end extensions and moved starts are covered. Calendar: 159 passed. |
| R04 | P2 / Camps | A lead assigned in a prior season must retain access to a pending renewal when cached years omit the assignment. Cache hits and misses must provide equivalent authorization facts. | Real repository/service/cache/authorization regression now passes; fix in [peterdrier/Humans#1712](https://github.com/peterdrier/Humans/pull/1712). |
| R05 | P2 / Workgroups | A future scheduled meeting must not grant credit for activity that has not happened and suppress overdue update/dormancy actions. | Fixed in [peterdrier/Humans#1715](https://github.com/peterdrier/Humans/pull/1715). Held-meeting boundaries, delayed daily processing, repeat execution and failed saves are covered; clearing records the approved audit action. |
| R06 | P2 / Base + Users | Shared Human avatar URLs must resolve to the moved ProfileView.Picture action. | Fixed in [peterdrier/Humans#1708](https://github.com/peterdrier/Humans/pull/1708), merged to main. Real MVC URL-generation regression now passes; [peterdrier/Humans#1712](https://github.com/peterdrier/Humans/pull/1712) carried an identical commit, now redundant after merging main. |
| R07 | P2 / Scanner | Lookup A starts, B succeeds, A fails later. B's display must remain. Every asynchronous completion must apply only to the current lookup. | Baseline failure and deployed fix verified in Chromium and module regressions. Fix and CI wiring in [peterdrier/Humans#1713](https://github.com/peterdrier/Humans/pull/1713), merged. |
| R08 | P2 / Rideshare | Server-rendered list interest controls must work when MapLibre construction, map loading or GeoJSON fetch fails. | Baseline failure and deployed fix verified in Chromium and module regressions. Fix and CI wiring in [peterdrier/Humans#1713](https://github.com/peterdrier/Humans/pull/1713), merged. |
| R09 | P2 / Workgroups | Settings and Register existing group must remain discoverable to authorized admins when the review queue is empty. | Fixed in [peterdrier/Humans#1715](https://github.com/peterdrier/Humans/pull/1715). Actual sidebar navigation verified on preview with empty queues; Board/Admin allowed, Volunteer denied. |
| R10 | P2 / Workgroups + Surveys | Reject excessive ordinary form input before persistence: Workgroup log body 16,000; lifecycle reasons 4,000; survey rejection note 4,000. Explain validation and preserve input. | Fixed in [peterdrier/Humans#1715](https://github.com/peterdrier/Humans/pull/1715). Service limits and invalid member/admin form input retention covered by regression tests. |
| R11 | P2 / Surveys | Submit and ranked recalculation controls must reflect the actual resource authorization for their viewer. Server denials remain enforced. | Fixed in [peterdrier/Humans#1715](https://github.com/peterdrier/Humans/pull/1715). Controls use actual resource authorization; invalid saves recalculate authorization and retain input. |
| R12 | P2 / Workgroups | Meeting LocationUrl field, model and schema limits must agree and server errors must be visible. | Fixed in [peterdrier/Humans#1715](https://github.com/peterdrier/Humans/pull/1715). Form/model/service consistently enforce 2,000 characters and render validation errors. |
| R13 | Policy/copy / Workgroups | Private-meeting wording must match the register's actual audience. Approved design allows approved humans to read register meetings/minutes; UI says member-only. IsPublic gates community-calendar listing. | Fixed in [peterdrier/Humans#1715](https://github.com/peterdrier/Humans/pull/1715). Broad register access retained; checkbox/help clarify calendar listing in all supported cultures. |
| R14 | P2 / Shifts | A non-Active actor can reach signup through exempt onboarding/dietary endpoints. Enforce actor eligibility in the owning signup service for single and range signup; preserve named Active pre-consent onboarding and explicit on-behalf workflows. | Fixed in [peterdrier/Humans#1716](https://github.com/peterdrier/Humans/pull/1716). Sixteen invalid-account cases and the development-seeder regression reproduced before implementation. Shifts 610 and Development 18 tests pass; independent review accepted. |
| R15 | Recovery documentation | Restore instructions must identify the correct backup, compatible PostgreSQL tools, release-specific migration histories and matching rollback image. Snapshot suffixes alone cannot identify a deploy. | Corrected in this PR after source audit and independent review. Separate PostgreSQL 16 client/server fixtures verified custom/SQL scratch and replacement restores, actual migration-ID output and failure stopping. No production commands were run; the final rehearsal below remains required. |

## Work loop

1. Reverify the finding against current code and prior maintainer decisions.
2. Reproduce the invariant with a meaningful regression test; exercise the actual filter, cache, expander or browser module where the defect lives.
3. Fix at the source using existing section-owned surface. No architecture bypasses, runtime-state edits or speculative adjacent features.
4. Verify applicable negative authorization, audit, GDPR, cultures, invariant docs, migrations and navigation. Explain non-applicable surfaces in the PR.
5. Independently review the final diff, then publish a focused PR against fork main. Every lane branches from `origin/main`, with its own worktree; no stacked lane dependencies.
6. Triage actual CI/review events under the `pd:steward` skill and `.claude/steward.md`; preserve dispositions and the review-round ceiling. Peter merges.
7. Record final combined verification and residual release decisions here before production promotion.

Parallel ownership: GoogleIntegration permissions; Users/Web/Base/Camps/Calendar; Workgroups/Surveys; Scanner/Rideshare and release coordination. Compiler use is serialized across lanes.

## Initial review evidence and remaining release gates

- At the original reviewed fork snapshot, full solution build and tests passed. Four initial Web failures passed targeted and whole-project reruns, then the final solution run; their initial cause was not established. This is not the final combined fix gate.
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

1. Restore a recent production backup into an isolated rehearsal database matching the actual production server major. Record server/dump/restore versions, backup timestamp and source release; verify the archive with the selected restore client. QA's PostgreSQL 16 rehearsal is not proof for production.
2. Use an isolated `Staging` host to exercise pre-migration snapshots with supported integration stubs: clear Google credentials and explicitly clear `Email:SmtpHost` (removing SMTP credentials alone leaves the default real relay selected). Isolate all outbound credentials/network access before first boot; background jobs start outside `Testing`. Give the rehearsal its own uploads and snapshot volumes. Verify the effective database host/name after `docker-entrypoint.sh`; inherited preview metadata and `DB_PASSWORD` can override a supplied connection string. Start the candidate normally so its section-baseline reconciliation and migrations follow the deployment path. Record migration identities for each old/new release's registered contexts, before/after row counts, startup logs and `/api/version`.
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
- R13: Peter chose broad register access. The checkbox controls community-calendar listing; meeting and minutes access stays unchanged. Updated copy covers all supported cultures.

## Fix verification updates

All application fix lanes passed independent source review, full build and full solution tests. No corrective PR adds a migration.

| PR / scope | Published commit | Final local verification |
| --- | --- | --- |
| [1712: People, Calendar, Camps, avatars](https://github.com/peterdrier/Humans/pull/1712) — open | `ef0c7a6f5f9dba1a27f772d78019ce39dd759e63` | 6,595 passed; zero failures. Three review rounds. |
| [1713: Scanner, Rideshare](https://github.com/peterdrier/Humans/pull/1713) — merged | `b9e2b71a285e6646bead5c14c2a3c5b6803f7d16` | Full solution passed; nine Node regressions passed. |
| [1714: Drive access](https://github.com/peterdrier/Humans/pull/1714) — merged | `87f2289cd4343e87c56552d35b7bdcff25277be1` | 6,563 passed; zero failures. One review round. |
| [1715: Workgroups, Surveys](https://github.com/peterdrier/Humans/pull/1715) — merged | `c53367dfe9d1bb280914cac968ef3a4378b694c7` | 6,570 passed; zero failures. One review round. |
| [1716: Shifts eligibility](https://github.com/peterdrier/Humans/pull/1716) — merged | `abed5c891cd61f70dd69c997e3abe1d24557bd99` | 6,557 passed; zero failures. |

- Earlier full runs had timeout-only failures, primarily in Web tests. Unchanged full retries passed; no timeouts were raised and no tests were disabled or excluded. Captured stacks showed continuing reflection/resource/model work and idle workers, but did not establish a cause. These failures are recorded, not explained away as a proven infrastructure defect.
- People validation uses executable composed name/membership filters, actual MVC avatar URL generation, and Calendar/Camps/Users regressions. No deployed Profile/terminal-account browser journey was run. The final Calendar review reproduced 14 failures, then changed expansion to validate end-only identities through the current recurrence rule while preserving genuine start moves.
- Authenticated Chromium reproduced Scanner/Rideshare baseline failures, then verified the actual deployed scripts on preview1713 at `ecf13d2e` with controlled network/map failures. Final `3cbdfbb96` adds the Node 24 CI step to those same application scripts. Synthetic Rideshare fixtures used ordinary forms; no database edits. The exact CI command, `node --test 'tests/Humans.*.Tests/Browser/*.test.*'`, also passed on the combined branch.
- Workgroups preview1715 at final `7a5812d7` verified Board/Admin sidebar, bootstrap and settings access with empty queues and denied Volunteer admin access. The final read-only browser check passed for each role. Cross-review also corrected two invalid admin form paths that discarded submitted input. The final review commit reuses existing policy helpers on Survey breadcrumbs and completes audit documentation.
- Drive tests exercise real SDK requests against inert HTTP transport; no live Google permission writes. Shifts checks are automated service/seeder regressions, not a deployed browser journey.
- `GET /api/version` on all PR previews confirmed the published commits listed above. Version checks do not substitute for the browser journeys described separately.

### Combined gate

Scratch integration commit `7e2771f4fe0909c2cfef3e78f7f87243a5e9dfbc` combines the published application heads above on the pinned fork main. Full build, formatting, diff whitespace checks, all nine browser regressions and all **6,665 .NET tests** passed. The first run at this commit had 32 execution timeouts across Agent, Camps, Web and Workgroups; the unchanged full retry passed with zero failures. No source, test selection or timeout settings changed between those runs.

All application PRs have passing CI on their listed commits and no unresolved review findings. The Calendar follow-up is included in this final combined result. The integration branch is a local verification artifact; the source changes are published in the PRs above.

The Shifts documentation sentence was moved so its PR and the People PR merge cleanly in either order; both invariants remain in the combined tree. The final production rehearsal still targets the eventual merged fork commit, not this scratch verification commit.
