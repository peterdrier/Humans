# Controller Intent Audit — 2026-06-12

**What:** every controller in `src/Humans.Web/Controllers/` (89 files, ~25.6k lines) audited for business logic by **intention**, not metric. Litmus per statement: *would a Hangfire job doing the same domain operation need this line?* Yes → it belongs in the section's service.

**How:** 12 parallel read-only auditors (sonnet), grouped by section; headline findings spot-verified against source by the orchestrator. **No code was changed.** Every "surface change needed" below is a proposal requiring Peter's per-item approval (`interface-method-additions-are-debt`).

**Confidence labels:** `clear` = unambiguous domain decision in Web; `borderline` = defensible either way, judgment call.

---

## Cross-cutting themes (the patterns behind the findings)

1. **Post-mutation workflow sequencing in controllers** — the largest class. A controller calls a mutation, then decides what domain side-effects follow: Teams coordinator-sync trio, Google-group compensation on team create/edit, event lifecycle emails on submit and on moderation, ProfileController.Edit's 8-step save chain. A Hangfire job doing the same mutation would silently skip these steps.
2. **State-transition eligibility predicates duplicated in Web** — `CanEdit`/`CanWithdraw`/`CanApproveReject` status-set predicates re-derived in controllers while the entity/service holds (or should hold) the authoritative rule: Events (4 sites, with an undocumented barrio-vs-individual asymmetry), Expenses, Governance applications, Issues open/closed sets.
3. **Read-DTO enrichment gaps** — most fixes here are *one computed flag on an existing canonical DTO* (`CanWithdraw` on the expense DTO, `IsWorkspaceCanonical`/`CanUnlink` on `UserEmailEditDto`, `CanBePublic` on `TeamInfo`, `IsReporter` on `FeedbackMessageInfo`), not new methods. This matches the read-model-enrichment rule.
4. **The same rule written twice in the same file** — last-verified-sign-in invariant (ProfileController ×2), open-season selection (CampController GET+POST), edit-state guards (EventsController GET+POST ×2 flows), `isApprovedColaborador` (GovernanceApplications ×2).

## Highest-priority individual findings

| # | Finding | Why it leads |
|---|---|---|
| 1 | **CampCompliance role matching by name string** | Correctness risk: rename a role definition → compliance matrix silently shows everyone unassigned |
| 2 | **OnboardingWidget self-heal mutates lifecycle state on GET** | State transition (Suspended→Active) fired from a read path |
| 3 | **Teams coordinator-sync trio** | Three call sites deciding when a system team needs reconciling; one architectural fix |
| 4 | **SEPA generate ordering invariant in controller** | Retry-safety of a payment workflow guaranteed only by controller code shape |
| 5 | **AccountController LastLoginAt scatter (7 sites) + OAuth provisioning chains** | Most pervasive single rule; provisioning chain already has a service-side twin for magic links |
| 6 | **ScannerController feed assembly** | 30-line domain aggregation duplicating occurrence-expansion + Uid format owned by EventService |
| 7 | **Events lifecycle notifications in controllers** | Bulk-import path provably skips them today — the divergence the litmus predicts |

---

## Findings by section

### Profile (ProfileController, ProfileApi, ProfileAdmin, ProfileBackfillAdmin, ProfilePictureMigrationAdmin)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| P1. "Other" allergy ⇒ free text required — cross-field invariant duplicated in Edit POST and DietaryMedical POST | `ProfileController.cs:249-253`, `1583` | borderline | `IProfileEditorService` — validation guard in `SaveProfileAsync` (ValidationException); drop both controller copies |
| P2. Burner-CV completeness rule (≥1 CV entry OR NoPriorBurnExperience) | `ProfileController.cs:262-275` | clear | `IProfileEditorService.SaveProfileAsync` — enforce internally (request already carries the flag) |
| P3. Initial-setup tier-application workflow: `isInitialSetup` derivation, tier⇒required-fields rules, submit-vs-update-draft dispatch, cancel-pending-deletion | `ProfileController.cs:278-387` | clear | `IProfileEditorService` — `SaveInitialProfileWithApplicationAsync(...)` (or extend `SaveProfileAsync` with optional application payload) owning the whole sequence |
| P4. Edit POST sequences 8 mutations; the after-save domain side-effects (consent-check trigger, deletion cancel, application submit) are workflow, not HTTP | `ProfileController.cs:352-456` | clear | fold side-effects 2–4 into `SaveProfileAsync` (with P3); contact-fields/languages/tags saves are page-specific and may stay |
| P5. Last-verified-sign-in-method invariant before unlink (verifiedAfter < 1 → block); service `UnlinkAsync` has **no** guard, so programmatic unlink can lock a user out | `ProfileController.cs:960-969` | clear | `IUserEmailService.UnlinkAsync` — enforce verified-count floor internally |
| P6. Always-on category guard (`IsAlwaysOn()` → BadRequest) only in controller | `ProfileController.cs:1698-1699` | clear | `ICommunicationPreferenceService.UpdatePreferenceAsync` throws; controller maps. No new method |
| P7. Dietary-save → shift-signup replay workflow (incl. privileged-flag computation) | `ProfileController.cs:1613-1659` | borderline | possibly `IShiftSignupService.TryReplayShiftSignupAfterDietarySaveAsync`; one call site — Peter's call |
| P8. `ShiftInfoViewModel.MergeSkills/MergePersistedQuirks/MergeLanguages` — canonical storage-encoding rules ("Other: <text>", legacy-value preservation) live on a Web VM | `ProfileController.cs:1511-1517` | clear | `IShiftManagementService.UpdateShiftPreferencesAsync(userId, skills, timePreference, quirks, languages)` — merge+encode inside; VM helpers deleted |
| P9. Workspace-lock predicate (Provider=Google AND @workspace-domain) recomputed in Web | `ProfileController.cs:2122-2130` | borderline | `IUserEmailService` — `IsWorkspaceCanonical` computed flag on `UserEmailEditDto` |
| P10. `CanUnlink` per-row computation (same invariant as P5) | `ProfileController.cs:2160-2177` | clear | with P5 — batch-aware `CanUnlink` on `UserEmailEditDto` |

Notes: ProfileBackfillAdmin tombstone filter (mild; service is idempotent); ProfileApi contact-field display-priority order (soft, one call site); `Me` Withdrawn-application filter is defensible presentation.

### Camps (Camp, CampAdmin, CampApi, CampCompliance, Cantina, Container)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| C1. "Most recent open season = registration target" computed in GET **and** POST (can diverge) | `CampController.cs:146-150, 336-342` | clear | `CurrentRegistrationYear` computed property on `CampSettingsInfo` |
| C2. AssignRole resolves "open season = Status==Active" in controller; sibling `AssignRoleByUser` already delegates to `AddMemberAndAssignRoleInActiveSeasonAsync` — same rule owned by two layers | `CampController.cs:993-997` | clear | `ICampRoleService.AssignAsync` overload taking campId, resolving active season internally |
| C3. Contact POST picks "leads of the latest season" as recipients | `CampController.cs:263-269` | borderline | `ICampContactService.SendFacilitatedMessageAsync(campId, …)` resolving leads internally — unless caller-supplied leads was deliberate design |
| C4. "My camps" = lead's seasons where Status ∉ {Active, Full} — status-set eligibility | `CampController.cs:90-101` | borderline | `ICampService` — `IsVisibleToLeadOnly` flag on `CampSeasonInfo` or a query method |
| C5. Pending-review badge count derived by iterating all seasons | `CampController.cs:104-106` | borderline | marginal; service method or static helper, low priority |
| C6. **Compliance matrix joins members to roles by role NAME string** (`m.Roles.Contains(col.Name)`) — rename a role definition and the matrix silently empties | `CampComplianceController.cs:48-55` | clear | `ICampRoleService.BuildComplianceMatrixAsync(year, …)` joining by `CampRoleDefinitionId` |
| C7. `ParseCampLinks` — URL validation + Platform enrichment; also a layer-skip (controller calls Domain `PlatformDetector` directly) | `CampController.cs:1285-1301` | borderline | `ICampService` (Create/Update accept raw URLs) or Domain factory `CampLink.FromUrl` |

Clean: directory filters, all VM mappers, CanSeeFullCamp, Cantina (offsets service-owned by design), Container.

### Teams (TeamAdmin, Team)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| T1–T3. **Coordinator system-team sync trio**: RemoveMember inspects `wasCoordinator` then syncs; AssignRole syncs unconditionally; UnassignRole does lookup→mutate→sync | `TeamAdminController.cs:225-229, 826-828, 851-860` | clear | one architectural fix — `ITeamService` owns the Coordinators sync as a post-mutation side effect (DI `ISystemTeamSync` or outbox/event; `GoogleSyncOutbox` precedent exists); drop the returned bool |
| T4. `CanManageRoles = !team.IsSystemTeam`; `CanProvisionEmails = true` hardcoded | `TeamAdminController.cs:193-195` | borderline | `CanManageRoles` on `TeamInfo`; discuss whether CanProvisionEmails has a real rule |
| T5. `canBePublic = !IsSystemTeam && !ParentTeamId.HasValue` duplicated ×2 | `TeamAdminController.cs:884, 1228` | clear | computed `CanBePublic` on `TeamInfo` (precedent: `GoogleGroupEmail`) |
| T6. "Resource authority lives at department level" (`ParentTeamId ?? team.Id`) | `TeamAdminController.cs:1214-1222` | clear | `ITeamResourceService.CanManageTeamResourcesAsync` resolves parent scope internally |
| T7. Provision-email membership pre-check via GetTeamAsync + Members.All | `TeamAdminController.cs:288-293` | borderline | `IEmailProvisioningService.ProvisionNobodiesEmailAsync` takes teamId, validates inside (typed result) |
| T8. `DrivePermissionLevel.None` not-settable guard | `TeamAdminController.cs:501-505` | borderline | `UpdatePermissionLevelAsync` rejects internally |
| T9. **Child-team member rollup** (grandfathered HUM0031): union of direct + child members with dedup, coordinators → SubteamLeads; two service calls composed in Web | `TeamController.cs:173-229` | clear | `ITeamPageService.GetTeamPageDetailAsync` computes it; extend `TeamPageDetailResult` with `SubteamLeads`/`ChildTeamMembers`; controller drops to two `.Select()`s |
| T10. `CanOpenStore = (coordinator && top-level && active) ∥ admin-roles` | `TeamController.cs:161-163` | clear | domain part on `TeamPageDetailResult`; controller ORs the role claims |
| T11. Redundant second `GetTeamAsync` solely for `EarlyEntryEnabled` | `TeamController.cs:168-171` | borderline | DTO field on `TeamPageTeamSummary`; no method addition |
| T12. Pre-join eligibility (system-team / already-member / pending-request) with inline membership LINQ | `TeamController.cs:444-469` | borderline | `IsMember(userId)`/`CanJoin(userId)` on `TeamInfo`; lower priority |
| T13. Join POST branches on `team.RequiresApproval` → request vs direct join | `TeamController.cs:510-519` | clear | `ITeamService.JoinTeamAsync(teamId, userId, message)` dispatching internally, discriminated result for the flash message |
| T14. `IsSensitive` admin-only mutation policy (null = leave-unchanged) | `TeamController.cs:753-755` | clear | `UpdateTeamAsync` takes actor identity, enforces internally; medium priority |
| T15. Create/Edit team → Google-group provision → compensate-on-failure (clear prefix) — same pattern in both POSTs | `TeamController.cs:657-669, 762-784` | clear | service-owned `CreateTeamWithGoogleGroupAsync` (and update twin) owning the compensation |

### Events (Events, EventsModeration, EventsAdmin, EventsExport, EventsDashboard, Calendar)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| E1. `CanEdit`/`CanWithdraw` status-set predicates stamped onto rows | `EventsController.cs:88-89, 120-123` | clear | computed `CanEdit`/`CanWithdraw` on `EventInfo` (or `EventStatus` extensions in Domain) |
| E2–E4. Edit/withdraw state guards duplicated across GET+POST for individual AND barrio flows — **with an asymmetry the entity does not enforce** (barrio excludes Draft from both sets; `Event.Withdraw()` allows Draft for all) | `EventsController.cs:226-230, 280-284, 346-350, 753-757, 799-803, 863-867` | clear | same predicate as E1, parameterized by camp-vs-individual; service mutation methods enforce, controller catches |
| E5. **All-day ⇒ 1440 min / midnight start** applied in Create, Update, and Moderation edit (3 sites) | `EventsController.cs:171-172, 304-305`; `EventsModerationController.cs:309, 314` | clear | command DTOs carry `isAllDay`; service/entity resolves the meaning |
| E6. Post-submission lifecycle email fired by controller — **bulk-import path provably skips it** | `EventsController.cs:197-209, 730-738` | clear | fold into `SubmitEventAsync` (actionUrl as parameter); remove `IEmailService`/`IEmailMessageFactory` from the controller |
| E7. Post-moderation notification workflow (actionType→status mapping, submitter lookup, email) — grandfathered HUM0031 driver | `EventsModerationController.cs:361-399` | clear | fold into `ApplyModerationAsync` |
| E8. "Must be Pending to moderate" pre-guard (entity already throws) | `EventsModerationController.cs:342-346` | borderline | discriminated result from `ApplyModerationAsync`; low urgency |
| E9. `PriorityRank == 0` ⇒ sort-to-end sentinel semantics in print guide | `EventsExportController.cs:125` | clear | `GetApprovedEventsForPrintAsync(maxSlots)` applying the rule internally |
| E10. `PriorityRank == 0 ? 1` defaulting in moderation edit form | `EventsModerationController.cs:189` | borderline | with E9 — sentinel handling on DTO/domain |
| E11. `DisplayHost` resolution (camp vs individual; CSV export already diverges) | `EventsController.cs:527-529` | clear | computed field on `ApprovedEventView` at cache-warm |
| E12–E13. Interval-overlap algorithm written twice: schedule conflicts (O(n²)) + moderation duplicate candidates | `EventsController.cs:422-436`; `EventsModerationController.cs:77-91` | clear | shared Application-layer `EventConflictDetector` used by both |

Notes: occurrence-expansion call-sites (5 places with a repeated fallback pattern) — reuse smell, judgment call; Calendar all-day half-open storage convention applied at form-mapping — borderline; Calendar 19:00/20:00 form defaults — UX convenience.

### Money (Expenses, Finance, Budget, Store, StoreAdmin, StoreStripeWebhook)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| M1. `canWithdraw` status set (Submitted/CoordinatorEndorsed/Approved) duplicated from service-internal guard | `ExpensesController.cs:197-199` | clear | `CanWithdraw` flag on the expense read DTO |
| M2. SEPA eligibility pre-filter (`Status != Approved → skip`) in controller; per-report auth check is correct and stays | `ExpensesController.cs:780-794` | clear | `FilterSepaEligibleAsync(ids)` on the service; controller keeps authorization |
| M3. **SEPA ordering invariant** ("XML before flip ⇒ retry-safe") guaranteed only by controller code shape | `ExpensesController.cs:752-754` | clear | `GenerateSepaPayoutAsync(reportIds, actorUserId)` returning (xml, fileName, flippedIds) — one domain op (compose with M2) |
| M4. Expense-only runway model (gross-ticket-revenue seed; income lines don't extend it; fundsExhausted) + inline cross-section revenue sum (pattern already exists in `TicketingBudgetService.cs:30`) | `FinanceController.cs:120-137, 644-723` | borderline→clear | `ITicketServiceRead.GetGrossTicketRevenueAsync()`; runway → `IBudgetService`. Week/month grouping stays |
| M5. Delete-order affordance gated by `BalanceEur == 0m` (service enforces same rule as defense-in-depth) | `StoreController.cs:83` | borderline | `CanDelete` flag on `StoreOrderPageData` |

Notes: StoreAdmin 21% Spanish VAT form-default const in controller — worth a domain/config constant, not a finding. StoreStripeWebhook: clean.

### Shifts (ShiftAdmin, Shifts, ShiftDashboard, ShiftWorkloadAdmin, VolunteerTracking)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| S1. Dietary signup gate (`QualifiesForCantinaMeal` + empty DietaryPreference → block signup) enforced pre-call in controller | `ShiftsController.cs:190-195, 295-300` | clear | `SignUpAsync` returns `DietaryRequired` result code; controller pattern-matches to redirect |
| S2. **ToggleDay semantics** (grandfathered HUM0031): active-signup resolution by status set + signup-vs-bail branch | `ShiftsController.cs:186-213` | clear | `IShiftSignupService.ToggleDayAsync(userId, shiftId, flags)` — typed result absorbing S1 too |
| S3. "Signups blocked by missing dietary" derived predicate used ×3 | `ShiftsController.cs:289-293` | clear | `IShiftManagementService.IsSignupBlockedByMissingDietaryAsync(...)` (extends existing surface) |
| S4. iCal token lazy-provisioning (`if null → new Guid → persist`) in Mine | `ShiftsController.cs:393-400` | clear | `IUserService.GetOrCreateICalTokenAsync(userId)`; `RegenerateIcal`'s deliberate reset stays |
| S5. CreateRota constructs full entity incl. `Guid.NewGuid()` + `clock` stamp | `ShiftAdminController.cs:86-100` | borderline | `CreateRotaInput` DTO (mirrors `CreateShiftInput`); controller loses its `IClock` |
| S6. ToggleVisibility flips entity field directly then generic update | `ShiftAdminController.cs:286-293` | borderline | `ToggleRotaVisibilityAsync(rotaId)` returning new value |
| S7. Export date-range resolution: 4-branch cascade over period/subPeriod/offsets + swap guard, using Web-layer `ShiftFilterResolver` + Domain `BuildSubPeriodClassifier` | `VolunteerTrackingController.cs:137-163` | clear | resolution into `IVolunteerTrackingExportService.BuildAsync` (request carries raw period/subPeriod) or Application-layer resolver |
| S8. Rota recipient preview re-derives the send audience (Pending∨Confirmed) the service applies on send | `ShiftAdminController.cs:384-401` | borderline | `GetRotaRecipientPreviewAsync(rotaId)` symmetric with existing team-level preview |

### Auth (Account, Google, DevLogin, Unsubscribe, Language, Search)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| A1. **`LastLoginAt` stamped at 7 sites** across AccountController | `AccountController.cs:114, 138, 199, 234, 275, 473, 598` | clear | `IUserService.RecordLoginAsync(userId)` — one consolidated change (stamp + cache invalidation) |
| A2. Locked-account OAuth relink workflow (eligibility + Identity mutations + reconcile) in a private helper | `AccountController.cs:161-218` | clear | `IAccountProvisioningService.RelinkLockedOAuthLoginAsync(...)`; controller keeps only `SignInAsync` |
| A3. OAuth signup provisioning chain (Create→AddLogin→Reconcile→EnsureStubProfile + `DisplayName = name ?? email`) — magic-link twin already lives in `AccountProvisioningService` | `AccountController.cs:263-298` | clear | `CompleteOAuthSignupAsync(...)`; rollback-on-CrossUserBlocked moves with it |
| A4. Magic-link TTL (15 min) + Europe/Madrid hardcodes duplicating service-owned facts | `AccountController.cs:444-447` | borderline | `SendMagicLinkAsync` returns `ExpiresAt`; timezone → config |
| A5. Batch drift-remediation orchestration (enumerate→remediate-each→aggregate) — the nightly-job shape | `GoogleController.cs:183-222` | clear | `IGoogleSyncService.RemediateAllDriftedGroupSettingsAsync()` returning summary |

All proposals respect the never-block-sign-in hard rule. GateLogin's `SystemUserIds.GateTerminalLoginName` routing comparison: flagged for awareness only.

### Feedback / Surveys (Issues, IssuesApi, Feedback, FeedbackApi, Survey, SurveyAdmin, SurveysApi)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| F1. Open/Closed = concrete status-set expansion in controller (Domain has `IsTerminal()` but controller re-expands) | `IssuesController.cs:47-52` | clear | `IssueStatusGroups.Active/.Terminal` Domain constants or service interprets the view mode |
| F2. Section-visibility rule (admin sees all; others role-owned sections) applied in controller | `IssuesController.cs:71-74` | clear | service/domain helper `GetAllowedSectionsForViewer(roles, isAdmin)` |
| F3. Reporter-filter scoping (non-admins only self) decided in controller | `IssuesController.cs:55-57` | clear | enforce inside `GetIssueListAsync` (already receives viewer identity) — implementation change, no new surface |
| F4. `IssueSectionInference.FromPath` — domain routing table living in `Humans.Web.Helpers` (second copy of `IssueSectionRouting`) | `IssuesController.cs:147` | borderline | infer inside `SubmitIssueAsync`; delete the Web helper |
| F5. Survey resume-draft reconstruction (CurrentPage selection, Started=true) outside `AdvanceWizardAsync` — violates the file's own stated contract | `SurveyController.cs:53-73` | clear | `ISurveyService.ResumeWizardStateAsync(...)` returning ready state |
| F6. Anonymity tier coercion (`AllowAnonymous ? requested : Identified`) | `SurveyController.cs:107` | clear | `SurveyWizardFlow.ResolveAnonymity(...)` or service enforces at start |
| F7. "Only Identified tier creates a draft" branch | `SurveyController.cs:113-117` | borderline | folds into F5/F6's `StartWizardAsync` |
| F8. GET-path page-skipping navigation (all-hidden page → advance or finish) parallel to `AdvanceWizardAsync` | `SurveyController.cs:313-321` | clear | `NormalizeWizardPageAsync(state)` on the service; controller switches on outcome |
| F9. `IsReporter` derivation duplicated ×3 (Feedback + FeedbackApi ×2) | `FeedbackApiController.cs:93, 110`; `FeedbackController.cs:332` | borderline | `bool IsReporter` on `FeedbackMessageInfo` — additive DTO field |

### Admin / Users / CityPlanning (CityPlanning, CityPlanningApi, UsersAdmin*, AuditLog, Admin*, Agent*)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| U1. Container `CanEdit` re-implements placement-eligibility (map-admin ∨ (window open ∧ own camp)) — fragment of `ContainerAuthorizationHandler` re-derived in a read path | `CityPlanningApiController.cs:206-209` | clear | `IContainerService.CanPlaceAsync(userId, campId)` or bulk editable-camp-IDs variant |
| U2. "Already merged into each other" pre-qualification from raw `MergedToUserId` fields — same precondition `ReconcileMergedRequestAsync` checks | `UsersAdminAccountMergesController.cs:38-40` | clear | `IsAlreadyMerged` computed on `AccountMergeRequestSnapshot` |
| U3. Container-placement GeoJSON structural contract (Feature/Polygon/center_lng/center_lat/rotation_degrees) validated in controller | `CityPlanningApiController.cs:358-377` | borderline | validate in `SavePlacementAsync` or Application value-object `ContainerPlacementGeoJson.IsValid` |
| U4. Dashboard recomputes `ActiveMembers` from raw snapshot (service already returns it) + computes TicketHolders cross-section | `AdminController.cs:33-37` | borderline | add `TicketHolders` to `AdminDashboardData`; use existing `ActiveMembers` |
| U5. GeoJSON export duplicates U1's ownership predicate (assembly itself is fine as download formatting) | `CityPlanningApiController.cs:217-259` | borderline | resolved by U1 |

Note: `FindUserLeadSeasonIdAsync`/`FindUserLeadCampIdAsync` duplicated verbatim across CityPlanning controller + API — consolidation candidate, not a violation per se.

### Tickets / Scanner / Guest (Ticket*, Scanner, EarlyEntryRoster, Guest, Debug, DevSeed)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| K1. VIP classification + taxable/donation price split computed in mapping (`Price > VipThresholdEuros` ×3) — needed by any report/export, absent from all service DTOs | `TicketController.cs:110-116` | clear | `IsVip`/`TaxableAmount`/`VipDonation` computed on the attendee row DTOs (page + export) in `TicketQueryService` |
| K2. "All transfers = union over every status" fan-out loop in controller | `TicketTransferAdminController.cs:33-37` | borderline | `GetAllAsync()` (or nullable status) on `ITicketTransferService` |
| K3. **Scanner door-context feed assembly**: personal-non-camp-events filter, occurrence expansion, `CalendarFeedItem` construction with the Uid format owned by `EventService.cs:587` — 30 lines of domain aggregation | `ScannerController.cs:109-139` | clear | `IICalFeedService.GetDoorContextItemsAsync(userId)` |
| K4. Ticketing-locked preference rule (`category == Ticketing && hasTicketOrder`) + `DefaultOptedOut()` fallback re-derivation (service's `IsOptedOutAsync` already encodes it); cross-section stitching in controller | `GuestController.cs:268-302` | borderline | richer preference DTO (`IsAlwaysOn`/`IsEditLocked`/`EffectiveOptedOut`) from `ICommunicationPreferenceService`, cross-section call inside the service |

### Governance / Onboarding / Legal (Governance*, Onboarding*, AdminLegalDocuments, Consent, Legal, Welcome)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| G1. `CanSubmitNew` / `IsApprovedColaborador` derived from raw status scans — duplicates what `IGovernanceIndexService` already returns for the sibling page | `GovernanceApplicationsController.cs:31-51` | clear | eligibility DTO from `IApplicationDecisionService` (or reuse `IGovernanceIndexService`) |
| G2. New-application defaulting: pending-guard + "Colaborador holder ⇒ default Asociado" tier-progression rule | `GovernanceApplicationsController.cs:65-78` | clear | `GetNewApplicationDefaultsAsync(userId)` → (CanSubmit, SuggestedTier); folds into G1's DTO |
| G3/G4. `CanWithdraw` / `CanApproveReject` = `Status == Submitted` in controllers | `GovernanceApplicationsController.cs:162, 266` | borderline | computed flags on the user/admin detail DTOs |
| G5. `HasBoardVotesAsync` pre-gate before Finalize — guard divorced from enforcement + TOCTOU window | `GovernanceBoardVotingController.cs:169-173` | clear | `ApproveAsync`/`RejectAsync` return `"NoVotes"` ErrorKey; drop the pre-check |
| G6. Legal-document "current version" temporal rule (`EffectiveFrom <= now`, MaxBy) — controller injects `IClock` solely for this | `AdminLegalDocumentsController.cs:115-118` | clear | `CurrentVersionNumber` pre-computed on the service DTO; drop `IClock` from controller |
| G7. Names POST idempotency guard on `HasRequiredNameFields` | `OnboardingWidgetController.cs:87-88` | borderline | `SaveOnboardingNamesAsync` checking internally |
| G8. **Consent self-heal on GET**: zero unsigned consents → `RestoreConsentSuspensionAsync` (Suspended→Active) fired from a read path | `OnboardingWidgetController.cs:191-197` | clear | move check-and-restore into `IOnboardingService` (e.g. `GetNextUnsignedConsentAsync` handling the heal); also consider whether the heal belongs on `SubmitConsentAsync`/reconciliation instead of any GET |
| G9. Consent progress ordinal arithmetic (`totalRequired - unsigned.Count + 1`) | `OnboardingWidgetController.cs:205-206` | borderline | `TotalRequired`/`CurrentIndex` on the service return |
| G10. Welcome routing: active → /Shifts, else → /OnboardingWidget (claim-projected domain workflow) | `WelcomeController.cs:14-20` | borderline | thin; optionally a `PostLoginDestination` from the onboarding state service |

### Misc (Home, Campaign, Notifications, Email, WidgetGallery, About, User, LogApi, …)

| Finding | Where | Conf. | Owning service → surface change |
|---|---|---|---|
| X1. Active-event-year resolution + `Year > 0` validity rule in private helper used by attendance actions | `HomeController.cs:157-167` | clear | `IShiftManagementService.GetActiveEventYearAsync()` (Shifts owns `EventSettings`) |
| X2. Undo-not-attending `false` interpreted as "locked by ticket sync" — domain reason encoded only in a controller string | `HomeController.cs:169-178` | borderline | richer result type from `UndoNotAttendingAsync` (Success + Reason) |
| X3. "Resolved filter incompatible with unread tab" coercion | `NotificationsController.cs:26-27` | borderline | normalize inside `GetInboxAsync` / typed query record |
| X4. Campaign code CSV tokenization rules (split/trim/drop-blank) — any import job would need them | `CampaignController.cs:138-144` | clear | `ImportCodesFromCsvAsync(campaignId, stream)` overload owning tokenization |

---

## Counter-findings — verdicts on the 15 HUM0031 grandfathers

| Grandfathered method | Verdict |
|---|---|
| `UsersAdminDebugController.ApplySort` | **Legit presentation.** Pure sort-column switch over VM properties; cc false positive. Threshold-calibration finding — grandfather stays. |
| `EmailController.EmailPreview` (51/2) | **Legit presentation.** Straight-line sample-email catalog for a dev preview screen; statement count = catalog breadth, zero domain decisions. Grandfather stays. |
| `EventsController.BulkUploadTemplate` (60/14, analyzer currently silent) | **Legit presentation.** Verbose CSV banner strings + field mapping; no domain decisions. (Also: its justification counts no longer match the analyzer — see ledger anomaly note.) |
| `AccountController.ExternalLoginCallback` (analyzer currently silent) | **Mixed.** Outer method is a sound dispatch hub; the domain logic sits in its private helpers (A2, A3). Extract those and the callback is legit session mechanics. |
| `ProfileController.BuildEmailsViewModelAsync` (46/27) | **Mostly legit.** Wide multi-source DTO assembly; two real findings inside (P9, P10). Move those and the remainder is honest controller turf. |
| `TeamAdminController.Members` (23/19) | **Mostly legit.** Pagination + projection + icon-lookup switch drive the count; one borderline finding inside (T4). |
| `EventsController.Browse` (39/27) | **Mostly legit.** cc driven by null-guards; one real finding (E11 DisplayHost). |
| `TeamController.Details` (33/19) | **Real debt.** The child-team rollup (T9) is the complexity; extract it and the rest is presentation. |
| `ShiftsController.ToggleDay` (38/20) | **Real debt.** Toggle semantics + dietary gate (S1, S2); the AJAX response shaping half is legit. |
| `EventsModerationController.ProcessActionAsync` (29/18) | **Real debt.** Notification workflow (E7) is ~⅔ of the body. |
| `EventsController.MySubmissions` (21/19) | **Real debt.** CanEdit/CanWithdraw predicates (E1). |
| `EventsController.Update` (42/17) / `BarrioUpdate` (41/11) | **Real debt.** State guards + all-day meaning (E2–E5). |
| `ProfileController.Edit` (78/46) | **Real debt.** The largest cluster in the codebase (P1–P4). |
| `ProfileController.DietaryMedical` (36/21) | **Mixed.** Validation invariant (P1) + borderline signup replay (P7); flash-message plumbing is legit. |

**Threshold calibration:** ApplySort (cc 16 from a sort switch) and EmailPreview (51 straight-line statements) are the two clean cases where the thresholds fire on legitimate presentation. The statement threshold punishes verbosity (catalogs, banners); the cc threshold punishes sort/mapping switches. Both grandfathers should simply stay.

---

## Suggested working order (by value)

1. **Correctness risks first:** C6 (role-name join), G8 (mutation on GET), G5 (TOCTOU), P5 (lockout-guard gap), M3 (SEPA ordering).
2. **One-fix-many-sites:** A1 (LastLoginAt ×7), T1–T3 (sync trio), E1–E4 (status predicates, 6 sites), E5 (all-day ×3), E12–E13 (overlap ×2).
3. **Workflow moves with existing service twins:** A3 (magic-link twin exists), E6/E7 (bulk-import divergence), P3/P4, T13, T15.
4. **DTO enrichment batch** (cheap, additive, no new methods): M1, P9/P10, T5/T10/T11, G1–G4, F9, K1, U2.
5. Everything borderline: per-item discussion.

Each item needs Peter's approval before any interface/surface change — none of this is actioned.
