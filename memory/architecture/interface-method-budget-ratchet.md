---
name: Interface method-count budget — strict down-only ratchet
description: HARD RULE. Adding a method to a budgeted interface requires removing one from the SAME interface in the SAME PR. No raises, no splits to dodge, no broader-replacement tricks. Only Peter authorizes exceptions.
---

The `[SurfaceBudget(N)]` attribute (in `src/Humans.Application/Architecture/`) + `SurfaceBudgetAnalyzer` (HUM0015 over-budget, HUM0016 slack) is a **consolidation mechanism**, not a paperwork-and-justification mechanism. Down-only. Strict.

**Why:** The audit-surface skill kept finding bloat that accrued one "+1 justified" PR at a time. A "split it" workaround would just redistribute the same surface across two budgets and unlock fresh growth runway in each. The point is to make budgeted interfaces *smaller over time* — not stable, not redistributed. Past agent justifications and split-and-grow patterns are the failure modes that put the ratchet in place.

**How to apply:**

- **No raises.** Don't increase a budget number for any reason. If you've added a method and the test is red, remove a method from the same interface to bring it back to budget — don't raise.
- **No splits to dodge.** Don't propose splitting a budgeted interface (e.g., extracting `ICampMembershipService` from `ICampService`) as a way to make room. Splits move methods into a fresh interface with a fresh budget — defeats the consolidation goal.
- **No replacement-by-broader-method tricks.** Don't replace a method with a more-general bag-of-options + flags one so the count drops by 1 while the surface area grows.
- **Add → remove from the SAME interface, same PR.** A new method on `ICampService` requires removing another method from `ICampService` in the same PR.
- **Net delta on the PR is ≤ 0.** When the net is negative, lower the budget number on the `[SurfaceBudget(N)]` attribute to match the new count exactly (HUM0016 enforces this — no headroom).
- **Hit a wall? STOP and ask Peter.** If a feature genuinely can't be expressed without growth, present the case and let him decide. Don't raise, split, or work around in the meantime.

**Scope:** all interfaces currently decorated with `[SurfaceBudget(N)]` (ITeamService, ICampService, IShiftManagementService, IProfileService, IUserService — grep for the attribute to verify the current set). The list of budgeted interfaces can grow (bringing more under the ratchet); no number on an existing entry goes up. The attribute is also valid on classes/structs, though only interfaces are budgeted today.

**Authorized exceptions log:**

- 2026-05-09 (issue #682): +1 each on `ITeamService`, `ICampService`, `IShiftManagementService` for `SearchAsync`. Queries against the owning section's tables must live in that section's service per design-rules §6 — moving them in is the consolidation goal, not a workaround.
- 2026-05-10 (PR #474, Peter explicit sign-off): +2 on `ITeamService` for canonical `GetTeamAsync(Guid)` / `GetTeamsAsync()` read-model methods that make the `CachingTeamService` decorator possible and establish the consolidation target for removing narrower team read helpers. This is temporary groundwork for the service-entity-boundary cleanup; follow-up Teams passes must reduce the budget back down by migrating callers to these methods and removing obsolete `Get*` helpers.
- 2026-05-10 (PR #474, Peter explicit sign-off): +1 on `ITeamRepository` for `GetAllWithMembersAsync`, needed by the `CachingTeamService` canonical team index because the previous active-only shape excluded inactive teams and `GetAllAsync` does not include members.
- 2026-05-10 (issue #490, Peter explicit sign-off): +3 on `ICampService` for the Early Entry camps consumer: `SetEeStartDateAsync`, `SetCampSeasonEeSlotCountAsync`, `SetEarlyEntryAsync`. EE state attaches to `CampSeason`/`CampMember`/`CampSettings`, all ICampService-owned tables, so the methods belong here per design-rules §6.
- 2026-05-11 (PR #491): `IProfileService` bumped 30 → 31 to add `SetIbanAsync` for the expense-report IBAN modal flow. Reason: IBAN is set/edited/cleared only via `/Expenses/{id}/Iban`, so the write belongs on the profile service that owns the IBAN column. No expiable substitute — the write must flow through the caching decorator and the audit log, which both require the `IProfileService` surface. Approved by Peter 2026-05-11.
- 2026-05-11 (PR #478, Peter explicit sign-off in PR discussion while closing issue #615): +2 on `ITeamResourceService` for `MarkResourceSyncedAsync` and `RecordResourceErrorAsync`. `google_resources` is Teams-owned, and moving `GoogleGroupSyncService` into Application.GoogleIntegration means cross-section sync status writes must route through the Teams resource boundary instead of injecting `IGoogleResourceRepository` into the Google Integration service.
- 2026-05-12 (PR #504, Peter explicit sign-off): `IUserService` bumped 30 → 31 to add `GetCountByContactSourceAsync` for the Mailer admin dashboard's per-source contact total. Reason: the dashboard tile needs a `User`-table count partitioned by `ContactSource`; deriving it client-side from `GetAllUsersAsync()` would force loading all 500 user records on every dashboard hit. Approved by Peter 2026-05-12.
