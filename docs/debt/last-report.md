# Debt Sweep Report — 2026-06-12

- **Branch:** `debt-sweep/2026-06-12T150757Z` (base `origin/main @ aaad87962`)
- **Budget:** 2h (default)
- **Themes worked:** `grandfathered-hum0024-nav-strip` (schema-blocked finding), `grandfathered-hum0031-controller-logic` (attempted, approach rejected, all code changes reverted)

## Fixed — 0 code items

The sweep removed 12 HUM0031 grandfathers across 11 commits, but **Peter
reviewed and rejected the approach, and all 11 commits were reverted**
(`d6c569c0a`). Every fix split the controller method into controller-local
private helpers / VM factories — satisfying the cc/statement metric while
moving **zero** domain decisions into services. Goodhart's law: worse than
nothing, because it removed the debt markers while keeping the debt.

All 15 `[Grandfathered("HUM0031")]` attributes are restored; the ledger entry
is back to 26 warning sites with corrected guidance.

### The lesson (now encoded in the ledger notes and the skill)

The rule is **"no business logic in controllers"** — the metric is only a
proxy detector. Controller turf: parse/bind, authorize, call service, shape
response (sort/filter/page, DTO→VM mapping, redirects, flash). Business
logic: decisions about the domain — state-transition rules, domain meanings
(all-day ⇒ 1440 min), workflow (who gets notified), derived domain facts,
toggle semantics. Litmus per statement: *would a Hangfire job doing the same
operation need this line?* Yes → it belongs in the service.

Only two valid fixes:

1. **Service move** — relocate the domain decisions into the section's
   application service. Interface surface changes need per-item Peter
   approval (skip-and-ask). Right-shaped existing pattern:
   `DietaryMedicalViewModel.ToCommand()` →
   `profileEditorService.SaveDietaryMedicalAsync(userId, command)`.
2. **Threshold-calibration finding** — the method is genuinely
   presentation (sorting/filtering/mapping) and just exceeds a low
   threshold. Say so; the grandfather stays.

Splitting a method into controller-local helpers is **forbidden**, full stop.

## Theme finding — `grandfathered-hum0024-nav-strip` is SCHEMA-BLOCKED

Verified empirically: removing the `HasOne` block from
`IssueCommentConfiguration` fires `dotnet ef migrations
has-pending-model-changes`. The config blocks own DB-level FK constraints +
cascade behavior, so **every** HUM0024 fix is an FK-drop migration — out of
sweep scope. Ledger marks the theme schema-blocked so rotation skips it.

## Skill fixes that survived the sweep

- Real `date -u` budget checks (`8016d1f3e`) — first run "estimated" 85 min
  elapsed while really at 27; never guess time.
- Anchored `[Grandfathered(` grep (landed earlier in PR #989).
- Forbidden-moves list now includes the controller-local-helper split
  (this sweep's lesson).

## Skipped

- All HUM0031 items — approach rejected; future sweeps need per-item Peter
  approval on service-move surface changes, so this theme is `review: panel`.

## Forbidden-move reverts

- 11 commits, 12 methods (`d6c569c0a`): controller-local helper splits across
  UsersAdminDebug/Events/EventsModeration/TeamAdmin/Team/Shifts/Email/Profile
  controllers — the entire HUM0031 work product of this sweep.

## Inbox additions

- Flaky integration test `UnprivilegedUser_AdminPostToOtherUserEmails_ReturnsForbid` — fails under full-suite parallel load (`TaskCanceledException` in cleanup), passes in isolation.

## Ledger changes

- `grandfathered-hum0024-nav-strip`: `last_swept: 2026-06-12`, schema-blocked note (remaining 34, untouched).
- `grandfathered-hum0031-controller-logic`: `last_swept: 2026-06-12`, `remaining: 13` (distinct warning sites; the earlier 26 was a raw grep double-counting each site in the build log), `review: light → panel`, notes rewritten with the valid-fix/forbidden-move guidance above.
- Count anomaly worth knowing: 15 grandfathers exist but only 13 fire — `AccountController.ExternalLoginCallback` (justification says 107 stmts / cc 33) and `EventsController.BulkUploadTemplate` (60 / 14) produce no HUM0031 warning today, so the analyzer's statement/cc counting disagrees with the counts frozen in those justifications.
- `recent_sections: []` (no code fixes survived).
- Header: explicit staleness-check exclusion for `NoDestructiveMigrationOps.baseline.txt` (immutable history, guard not backlog).
- Inbox: +1 (flaky test), now 10.

## Questions for Peter

1. HUM0024 nav strips all require FK-drop migrations — dedicated migration PR(s) outside the sweep, or park the theme indefinitely?
   **Resolved 2026-06-12: fine — handled via dedicated migration PRs outside the sweep; theme stays marked schema-blocked in the ledger so rotation skips it.**
2. Confirm the `NoDestructiveMigrationOps` staleness exclusion is right (its 23 baseline entries are historical migrations, not fixable debt).
   **Resolved 2026-06-12: confirmed.**
3. HUM0031 helper-split approach: **rejected 2026-06-12, reverted.** Follow-up is an intention-based controller audit (findings report, no code changes without per-item approval).
