<!-- freshness:triggers
  src/Sections/Humans.Onboarding/**
  src/Sections/Humans.Onboarding.Contracts/**
  src/Sections/Humans.Users/Services/HumanLifecycleService.cs
  src/Sections/Humans.Users/Services/ProfileService.cs
  src/Sections/Humans.Users/Services/UserService.cs
  src/Sections/Humans.Users/Services/AccountProvisioningService.cs
  src/Sections/Humans.Consent/Services/**
  src/Sections/Humans.Governance/Services/ApplicationDecisionService.cs
  src/Sections/Humans.Teams/Services/TeamService.cs
  src/Humans.Web/Controllers/AccountController.cs
  src/Sections/Humans.Users/Controllers/ProfileController.cs
  src/Humans.Web/Authorization/MembershipRequiredFilter.cs
  src/Humans.Web/Authorization/NameRequiredFilter.cs
  src/Sections/Humans.Shifts/Services/ShiftSignupService.cs
  src/Sections/Humans.Shifts/Controllers/ShiftsController.cs
  src/Sections/Humans.Governance/Views/**
  src/Sections/Humans.Consent/Section.cs
  src/Sections/Humans.Consent/Controllers/**
  src/Sections/Humans.Users/Controllers/UsersAdminController.cs
-->
<!-- freshness:flag-on-change
  Onboarding orchestration across Profile/Consent/Governance/Teams and the membership gate — review when OnboardingService or any of its cross-section dependencies change.
-->

# Onboarding — Section Invariants

Pure orchestrator over Profiles, Consent, Teams, and Governance. Owns no tables.

## Purpose

Humans is a large application, and most people meeting it are volunteers for an event. Onboarding welcomes people who are new to *using* Humans, whether or not their account is new (buying a ticket or joining the mailing list can create one), and gets them going without showing them the whole app at once: a short guided flow collects what the association needs to let them in, then points them at places they can help.

Where it points them depends on what the association needs right now. The shifts step exists because staffing shifts was the pressing need; off-season the better destination may simply be what is going on. Shifts stays one possible destination, not the definition of done.

> **Three-concerns split.** `OnboardingService` is the *intake funnel only* (signup → profile → consents → first-team admission, plus signup reject). The Consent Coordinator review queue is Consent's (`/Consent/ReviewQueue`), next to the `consent_reviews` table it writes. Sibling services own the other workflow stages so onboarding stays narrow:
>
> - **Lifecycle state-machine** (suspend/unsuspend, future re-consent suspensions and term-renewals) → `IHumanLifecycleService`.
> - **Board voting** → Governance's `ApplicationDecisionService`, injected by its own `GovernanceBoardVotingController` (Governance owns the `applications` + `board_votes` tables; OnboardingService carries no delegating wrappers).
> - **No dashboard aggregation lives here.** The review-queue badge is `ReviewQueuePillCountAsync` in Consent's `Section.cs`, counting `UserInfo.NeedsConsentReview` off `IUserService`; the member dashboard is section-contributed chrome.
> - **Account deletion cascade** → `IAccountDeletionService` (`src/Sections/Humans.Users.Contracts/IAccountDeletionService.cs`).

## Concepts

- **Onboarding** is the process a new human goes through to become an active Volunteer: sign up via Google OAuth, complete their profile, and consent to all required legal documents. Once both are done, admission to the Volunteers team is automatic — no Consent Coordinator approval is required for entry.
- The **access gate** is the stored `User.State` (`UserState`): the full application is reachable only when state is `Active` — i.e. the user has entered their legal name. `MembershipRequiredFilter` routes everyone else by state (`Bare` → name entry, `DeletePending` → cancel-deletion screen, `Suspended`/`Rejected`/`Deleted`/`Merged` → the account-status wall). A controller or action a non-`Active` account must still reach carries `[AllowBeforeActiveMembership]` in its own section. Access is **not** derived from Volunteers-team membership, and roles do not bypass the gate. A Backdoor-API-key-authenticated machine request skips the gate entirely — it carries no `UserState` claim (`src/Sections/Humans.Backdoor/Docs/Backdoor.md`).
- **Profileless accounts** are authenticated users with no Profile record (e.g., ticket holders, newsletter subscribers created by imports). With no legal name they are `UserState.Bare` and are routed to name entry, not to the Guest dashboard — `Guest` is not exempt from `NameRequiredFilter`. `/Guest` is reachable only by typing the URL, and only once a name is on file.
- The **Consent Coordinator review** (Consent's `consent_reviews`, read as `ConsentReviewInfo` through `IConsentService`, written through `IConsentWrite`) is a pure audit/annotation track. When all required consents are signed, its `Status` flips to `Pending` and the human appears in Consent's review queue. CC Clear / Flag still flip the review's `IsApproved` (Clear → true, Flag → false) for the annotation, but they have **no membership or access side-effects** — neither admission nor access depends on the status or `IsApproved`. Reject (the review's `RejectedAt` plus `UserState.Rejected`) is the only CC action that changes access.

## Data Model

This section owns no tables. Entity detail for the objects Onboarding reads / mutates lives in the owning sections: `src/Sections/Humans.Users/Docs/Users.md` (Profile, User), `src/Sections/Humans.Consent/Docs/Consent.md` (ConsentRecord, the consent review and `ConsentCheckStatus`), `src/Sections/Humans.Governance/Docs/Governance.md` (Application, BoardVote), `src/Sections/Humans.Teams/Docs/Teams.md` (TeamMember, Volunteers system team).

Onboarding-specific value type: `OnboardingResult`. What follows the member's own writes lives on `IOnboardingIntake`: `ProfileSavedAsync` (consent-check threshold, and the pending-deletion cancel before approval) and `ConsentSignedAsync` (consent-check threshold). The writing controller reports the write after `IProfileEditorService.SaveProfileAsync` or `ConsentService.SubmitConsentAsync`; Onboarding decides the consequences, so every surface gets the same ones. The leaf services never call back into Onboarding.

## Routing

Multiple controllers serve this section:

| Controller | Route | Notes |
|------------|-------|-------|
| `OnboardingWidgetController` | `GET /OnboardingWidget` | The dispatcher. Reads `IOnboardingWidgetState.GetCurrentStepAsync` and redirects to the step; `Complete` → `/`. Re-entry, refresh and the consent submit come back here; the steps themselves hand forward (Names → Shifts → Consents). |
| `OnboardingWidgetController` | `GET`/`POST /OnboardingWidget/Names` | Step 1. POST saves through `IProfileEditorService.SaveProfileAsync`; success peer-calls the threshold check, → Shifts. A refusal retains the names form with translated feedback, without a threshold check. CV is omitted so Users preserves it without validating unrelated data. |
| `OnboardingWidgetController` | `GET /OnboardingWidget/Shifts` | Step 2. Priority shifts for the active burn; renders Shifts' own list view component. |
| `OnboardingWidgetController` | `POST /OnboardingWidget/SignUp`, `.../SignUpRange`, `.../Skip` | Step 2 actions — single shift, multi-day Build/Strike rota, and the session-flag skip. All → Consents. |
| `OnboardingWidgetController` | `GET /OnboardingWidget/Consents` | Step 3. Next unsigned required document, rendered by Consent's `ConsentSigningStepViewComponent`, or Home with an error when its detail will not load (see `NextConsentStepOutcome`). The form posts to `/Consent/Submit`, which records the signature, peer-calls the threshold check and returns to the dispatcher. |
| `WelcomeController` | `GET /Welcome` | Anonymous explainer, including a localized invitation identifying the Tour as English-language content. Signed-in actives → `/Shifts`, signed-in non-actives → `/OnboardingWidget`. Routes on the `UserState` claim, **not** the widget step. |
| `GuestController` | `GET /Guest` | Reachable only by typing the URL, by a user who already has a name — `Guest` is not exempt from `NameRequiredFilter`, and nothing links here. Bounces a mid-widget user back to the dispatcher. Peter ruled its fate part of one onboarding redesign (nobodies-collective/Humans#1183). |
| `AccountController` | Login/logout/OAuth | Exempt from membership gate; no onboarding-specific routes |
| `MembershipRequiredFilter` | (global filter) | Routes by `UserState`: `Bare` → `/OnboardingWidget`, `DeletePending` → `/Gdpr/Deletion`, walled states → `/Gdpr/Status`, `Active` → app |
| `NameRequiredFilter` | (global filter) | Name-gate: redirects authenticated live accounts without a real BurnerName to `/OnboardingWidget/Names`; terminal accounts proceed to the membership-state wall |

Board voting is Governance's: `/Governance/BoardVoting`. Onboarding does not consume `IGovernanceService`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Unauthenticated visitor | Sign up via Google OAuth (or magic link) |
| Authenticated human (pre-approval) | Complete profile, sign legal documents, submit a tier application (optional), file an issue (the Help widget renders pre-approval, so `Issues` is exempt from the membership gate) |
| ConsentCoordinator | Reject signups from Consent's review queue, which calls `IOnboardingIntake.RejectSignupAsync` (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| Board, Admin | All ConsentCoordinator capabilities. Board voting on Colaborador/Asociado tier applications is Board+Admin only (`PolicyNames.BoardOrAdmin` / `PolicyNames.BoardOnly`). |
| UsersAdmin | No review-queue access; can reject a signup from the human admin page (`UsersPolicies.Manage`). |

## Invariants

- Single and range shift signup delegate visibility enforcement to Shifts: a retained hidden rota/shift ID cannot bypass staged signup rollout; a refusal returns to the shift step.
- Widget multi-day shift signup requires both day offsets and rejects missing or malformed
  values with 400 before enrollment; omitted bounds cannot silently select day zero.
  Explicit day zero remains valid.
- Widget step selection resolves the surviving user and requires their saved names before consent completion. Complete means names plus all required Volunteer consents; merged aliases use the survivor’s consent state, and shift signup stays optional.

- The dispatcher recognizes a returning member from any prior explicit consent signature, including a replaced document version and signatures on merged accounts. Missing current Volunteer consents send that member straight to Consents; names and current-consent completion are still checked first.
- The widget saves names to the resolved account whose profile it preserves. Its dispatcher and Shifts page read that account’s commitments, so a merge alias cannot hide an existing signup or send name edits to an archived profile.
- Consent-review eligibility checks and the Pending annotation write use the resolved survivor ID when called with a merged account ID; an archived profile is never annotated on the survivor’s behalf.

- Consent's `ReviewTextTooLong` refusal for reasons over 4,000 Unicode characters stops signup rejection before the Users state write, audits, deprovisioning, email or notification. Existing `AlreadyRejected` recovery continues using the recorded reason.
- Onboarding steps: (1) complete profile, (2) consent to all required global legal documents, (3) automatic admission to the Volunteers system team. CC review of the consent check is an independent audit track that runs in parallel — it does not gate admission.
- Volunteer onboarding is never blocked by tier applications — they are separate, parallel paths.
- Guest dashboard and widget-step reads propagate request cancellation through viewer, eligibility, consent, active-event, priority-shift browse and signup reads; an abandoned request stops rather than selecting a fallback step.
- Access is the stored `User.State`: the full app requires `UserState.Active` (legal name entered). It is NOT derived from Volunteers-team membership — nothing in-app should read Volunteers membership to decide access.
- The Guest dashboard carries the resolved viewer ID and renders its localized greeting through Human, without a profile link. Names are resolved and encoded at render time rather than copied into the dashboard model.
- Widget self-signups use Shifts’ default actor, chosen after resolving the signup account. A merged session cannot stamp its archived cookie ID onto the signup audit entry.
- Successful single and range widget signups retain the service's localized capacity or skipped-day warning in the information alert after redirecting to Consents.
- Shifts enforces `UserState.Active` for self-signups from the widget. Saving required names makes a new account eligible before consents; suspended, rejected, deletion-pending and terminal accounts cannot use those exempt endpoints to create signups.
- Volunteers admission is `HasRequiredNameFields && !IsSuspended && !review.IsRejected && HasAllRequiredConsentsForTeam(Volunteers)` — **name + consents**. The review's `Status` (incl. `Flagged`) and `IsApproved` are NOT consulted. Suspension and rejection remain the CC's kick-out levers: `RejectSignupAsync` records the rejection (`IConsentWrite.RecordRejectionAsync`, then `IUserWrite.SetRejectedAsync`) then calls `DeprovisionApprovalGatedSystemTeamsAsync`, so reject removes the user from Volunteers. A retry after only the Consent write landed (Consent answers `AlreadyRejected`, Users state not yet `Rejected`) finishes the state, audit, deprovision and notices with the recorded reason and reviewer; once the state is `Rejected`, a reject is refused with `AlreadyRejected` and writes nothing. Consent's flag is annotation-only and does not deprovision.
- Roles do not bypass the `UserState` access gate. Admin and coordinator tools require both `UserState.Active` and the relevant role/policy.
- OAuth login checks verified UserEmails, unverified UserEmails, and User.Email before creating a new account — preventing duplicate accounts when the same email exists on another user in any form.
- `OnboardingService` depends only on interfaces — no `DbContext`, `IDbContextFactory`, `DbSet<T>`, `IMemoryCache`, `IFullProfileInvalidator`, or repository.
- **No leaf-to-director callbacks.** Neither `ProfileService` nor `ConsentService` depends on `IOnboardingService`. The write reports (`ProfileSavedAsync`, `ConsentSignedAsync`) are declared on `IOnboardingIntake` (the leaf); `IOnboardingService` extends `IOnboardingIntake`. `ConsentController.Submit`, `ProfileController.Edit POST` and the reject actions of Consent's `ConsentReviewQueueController` and Users' `UsersAdminController` inject the leaf; the section's own `OnboardingWidgetController` injects `IOnboardingService`. Each controller (`ConsentController.Submit`, `ProfileController.Edit POST`, `OnboardingWidgetController.Names`) reports its write after the leaf write completes and sequences nothing itself. Director-to-leaf is one-way: `OnboardingService` writes through the owning leaf service during reject and during the threshold check, never the reverse. There is no admin-initiated volunteer approval: admission is name + consents, reconciled by `SystemTeamSyncJob`.
- Onboarding is completed via the `/OnboardingWidget` guided flow (Names → Shifts → Consents). Volunteers admission is reconciled by `SystemTeamSyncJob` on name + consents — `ConsentService.SubmitConsentAsync` fires no per-user team sync, so admission is eventually consistent via the batch job; access never depends on it.
- The shift-priority filter's accessible label is localized in all supported cultures.
- **Name-gate (entry invariant).** `NameRequiredFilter` (global action filter, `src/Humans.Web/Authorization/NameRequiredFilter.cs`) is the single gate that forces any *authenticated live account* whose profile has no real `BurnerName` — a Stub profile, or an Active profile with blank required names — to the burner + legal-name form at `OnboardingWidget/Names` before they can reach the rest of the app. It covers OAuth/Google first sign-in, imported contacts hitting the magic-link `ExistingUser` branch, and legacy blank-`BurnerName` accounts (nobodies-collective/Humans#812). It runs strictly *after* authentication and only ever redirects — it **never blocks sign-in**. `Deleted` and `Merged` accounts pass to `MembershipRequiredFilter`, which blocks name entry and all other recovery exemptions while keeping their status wall and session/language routes accessible. It keys on the cache-backed `UserInfo.HasRequiredNameFields` (refreshed on profile save), so the gate opens on the next request once the form is submitted. Exempt from the gate: the `Account` and `Language` controllers wholesale, plus the actions `OnboardingWidget/Names`, `Home/Error`, and `Home/Privacy`, any other `[AllowAnonymous]` endpoint (all but `Home/Index`), and a Backdoor-API-key-authenticated request (no name form for a machine to fill in).

## Negative Access Rules

- ConsentCoordinator **cannot** cast Board votes on tier applications and **cannot** finalize tier-application Approve/Reject decisions (Board+Admin only via `GovernanceBoardVotingController.Vote` / `Finalize`).
- Regular humans still onboarding **cannot** access most of the application (teams, shifts, budget, tickets, governance, etc.) until they enter their legal name (`UserState.Active`).
- Profileless accounts **cannot** access the Home dashboard, City Planning, Budget, Shifts, Governance, or any member-only features. They are redirected to the name form, not to the Guest dashboard — `NameRequiredFilter` runs before `MembershipRequiredFilter` and does not exempt `Guest`. **Exception:** profileless mid-widget users see the priority-shift list rendered inside `/OnboardingWidget` Step 2; direct navigation to `/Shifts` still routes them through the membership filter as today.

## Triggers

- Profile-save handling resolves the human and consent review once, sharing that snapshot between queue eligibility and pending-deletion withdrawal. When a human completes their profile and signs all required documents: the profile-save controller (`ProfileController.Edit` or `OnboardingWidgetController.Names`) calls `IProfileEditorService.SaveProfileAsync` then reports it with `IOnboardingIntake.ProfileSavedAsync`. If the member is not yet approved and has a pending deletion request, Onboarding cancels it through `IAccountDeletionService.CancelDeletionAsync` (finishing setup is a change of mind; an approved member's edit leaves the request alone). If the threshold predicate holds (profile eligible for review, no existing `ConsentCheckStatus`, `IConsentService.HasAllRequiredConsentsForTeamAsync(Volunteers)` returns true), it sets Consent's review `Status` to `Pending` through `IConsentWrite.SetConsentCheckPendingAsync`. This is the audit/annotation track — admission to Volunteers happens automatically and does not depend on this flow. The coordinator finds the queue through the `Review queue` pill in the admin sidebar (Consent's `Section.cs`), not through an inbox row.
- When a legal document is signed: the consent-submit controller (`ConsentController.Submit`, which the widget's consent step posts to as well) calls `ConsentService.SubmitConsentAsync` then reports it with `IOnboardingIntake.ConsentSignedAsync` (same threshold check from the Consent side). `SubmitConsentAsync` writes the `ConsentRecord` only; it fires no per-user team sync. Volunteers admission (`HasRequiredNameFields && !IsSuspended && RejectedAt is null && HasAllRequiredConsentsForTeam(Volunteers)`) is reconciled by `SystemTeamSyncJob`.
- When a consent check is flagged: the review's `IsApproved` is set to false and `Status = Flagged`. Annotation-only — no de-provisioning. The flag does not gate admission; it is a record nothing acts on.
- When a signup is rejected: Consent records `RejectedAt`, `RejectionReason` and `RejectedByUserId` and clears `IsApproved`; Users stores `UserState.Rejected`; system team memberships are de-provisioned (the rejection is the kick-out lever); a `SignupRejected` email and `ProfileRejected` notification are dispatched. Readers use `ConsentReviewInfo.IsRejected` or `UserInfo.IsRejected`.
- Rejection email preparation and dispatch are best-effort after mutation, audit and deprovisioning. Recipient lookup failures preserve the successful rejection and still attempt the in-app notice in English; requested cancellation propagates.
- The rejection email and in-app notice use the recipient’s supported preferred language (English fallback). The notice’s title, body and profile action are localized in all supported cultures; the reviewer’s free-text reason is excerpted only when needed to fit the 2,000-character body. The full reason remains in the profile, audit entry and rejection email.

## Cross-Section Dependencies

`OnboardingService` injects only what its methods need:

- **Audit actions:** declared on `OnboardingAuditActions` (`src/Sections/Humans.Onboarding/OnboardingAuditActions.cs`), one `AuditActionDefinition` each, written through `IAuditLogService`: `SignupRejected` (and the retired `VolunteerApproved`, declared so stored rows keep their verb). Consent audits clear/flag itself, inside its write.
- **Notifications:** `INotificationEmitter` (and `INotificationAutoResolve` where a source resolves). Its sources are declared in `OnboardingNotificationSources` (`src/Sections/Humans.Onboarding/OnboardingNotificationSources.cs`), one `NotificationSourceDefinition` each: `ProfileRejected` (informational, to the user: admin rejects signup); `VolunteerApproved` (retired: nothing emits it since admission became automatic, declared so stored rows still filter and purge).
- **Users:** `IUserService` — user reads, rejection email recipient. `IUserWrite.SetRejectedAsync` — the `UserState.Rejected` write on reject. Admin-initiated account purge is NOT here — it lives on `IAccountDeletionService`.
- **Teams:** `ISystemTeamSync` — Volunteers / Colaboradors / Asociados de-provisioning on reject (`DeprovisionApprovalGatedSystemTeamsAsync`).
- **Consent:** `IConsentService` — review reads, and the next unsigned document for the widget's consent step (`GetNextUnsignedConsentAsync`). `IConsentWrite` — the reject record and the `Pending` annotation. Clear and flag are Consent's own review-queue writes.
- **Lifecycle:** `IHumanLifecycleService` — used by `GetNextUnsignedConsentAsync` to self-heal a consent-suspended user who is already compliant (nothing left to sign after the required set shrank).
- **Notifications / Email:** Onboarding owns its one template — `OnboardingEmails` (internal) builds the `EmailMessage` from Onboarding's own `Onboarding_Email_*` keys in `OnboardingResource`, rendered in the recipient's culture via `CultureScope`, and `OnboardingEmailPreviews` (`IEmailPreviewContributor`, registered in `Section.Register`) lists it at `/Email/EmailPreview`; Email supplies transport only (`memory/architecture/email-templates-live-in-sender.md`, peterdrier/Humans#1651). `IEmailService.SendAsync` (with `OnboardingEmails.SignupRejected`), `INotificationEmitter` (`ProfileRejected`).
- **Cross-cutting:** `IAuditLogService` (the reject entry), `ILogger`.

## Architecture

**Owning services:** `OnboardingService` (intake funnel only).
**Sibling services in the three-concerns split:** `HumanLifecycleService` (state-machine), `ApplicationDecisionService` (board voting), `AccountDeletionService` (cascade — single entry point for `RequestDeletionAsync` and `CancelDeletionAsync`; both Gdpr's `MemberDataController` and `GuestDataController` deletion actions call through it; ticket-hold + 30-day-grace fields written atomically, see the cascade section of `src/Sections/Humans.Users/Docs/Users.md`).
**Owned tables:** None — orchestrator over Profiles, Consent, Teams, Governance.

### G5 shape

- **The contracts leaf is a project, not a folder, because Onboarding and Consent point both ways.** `Humans.Consent`'s `ConsentController` reports a submit with `IOnboardingIntake.ConsentSignedAsync`, and `Humans.Onboarding` names Consent's contracts and its `ConsentSigningStepViewComponent` (the widget's Consents step embeds it; Consent's strings stay inside Consent). `Humans.Consent` -> `Humans.Onboarding.Contracts` and `Humans.Onboarding` -> `Humans.Consent` is acyclic; a `Contracts/` folder would not have been.
- **The leaf is `IOnboardingIntake` and `OnboardingResult`.** `IOnboardingIntake` carries the write reports (`ProfileSavedAsync`, `ConsentSignedAsync`) and signup reject. `OnboardingResult` is on the leaf because the funnel's result vocabulary is returned by services in Users: `IUserWrite.SetRejectedAsync`, `IHumanLifecycleService`'s three transitions and `IAccountDeletionService`'s cancel/purge, the last two being Onboarding's own siblings in the three-concerns split. Everything else — `GetNextUnsignedConsentAsync`, `IOnboardingWidgetState` and the widget's session-state seam — is internal.
- **The shift step's rota tables are Shifts', not this section's.** `ShiftBrowseViewModel`, `RotaShiftGroup`, `ShiftBrowseMapper`, the `_BuildStrikeRotaTable` / `_EventRotaTable` partials and `OnboardingShiftsListViewComponent` are Shifts' presentation layer, in `src/Sections/Humans.Shifts/`. `Views/OnboardingWidget/Shifts.cshtml` invokes it by `Type` through the `IOnboardingShiftsStep` seam (`Contracts/IOnboardingShiftsStep.cs`, this project — not the `Humans.Onboarding.Contracts` leaf: the args record names `Humans.Shifts.Contracts` types, which the leaf cannot see without a cycle), contributed by Shifts' `IOnboardingShiftsStep` (nobodies-collective/Humans#1815). The event goes across by id; Shifts resolves its own settings DTO. The browse query carries the resolved viewer id, and Shifts returns each rota team’s effective early-entry cutoff using its signup-approval rule. Rota controls consume that fact; the closed-entry banner appears only when all listed early-entry rotas are locked. `OnboardingShiftsStepBuilder` (the step's stats line) is this section's.
- **`OnboardingProgressBannerViewComponent` is an `internal` type discovered by Shell's `SectionViewComponentFeatureProvider`, and is contributed to the `AboveContent` chrome slot by `Section.cs` rather than named in a Shell view. Its advisory state read receives the request-abort token; other lookup failures remain suppressed so the layout still renders.
- **The section's keys are `Onboarding_*`, in `OnboardingResource`.** Two callers outside the section inject `IStringLocalizer<OnboardingResource>`: Shifts' `ShiftsController` (`Onboarding_NameRequiredBeforeShifts`) and Governance's board-voting detail page (four `Onboarding_Review*` applicant-summary labels). Three keys deliberately live in `SharedResource` — `Onboarding_BurnerNameLabel`, `Onboarding_LegalFirstNameLabel`, `Onboarding_LegalLastNameLabel` are `[Display]` names resolved by MVC's global `DataAnnotationLocalizerProvider`, which is bound to `SharedResource` and cannot see a section's set.

- `OnboardingService` lives in `src/Sections/Humans.Onboarding/Services/OnboardingService.cs` and depends only on interfaces.
- `HumanLifecycleService` is Users' (`IHumanLifecycleService` on `Humans.Users.Contracts`): suspend, unsuspend and `RestoreConsentSuspensionAsync`, which this section calls from the consent step. Its detail is in `src/Sections/Humans.Users/Docs/Users.md`.
- **Decorator decision — no caching decorator.** Onboarding owns no cached data. Every cache invalidation for an Onboarding-driven write happens inside the owning section's service or decorator (Profile decorator refreshes `_byUserId` after reject/suspend/unsuspend; `ApplicationDecisionService.CastBoardVoteAsync` evicts the voter's own voting-badge count).
- **Cross-domain navs stripped:** N/A — Onboarding owns no entities.
- **DI direction is one-way.** `OnboardingService → IUserService` (and other leaves) only. No leaf depends on `IOnboardingService`. Reviewers should reject any new ctor dependency from `ProfileService` / `ConsentService` onto `IOnboardingService` (or any other director). A cycle fails `tests/Humans.Web.Tests/Infrastructure/SectionServiceResolutionTests`, which resolves every registered service.
- **Architecture test** — `tests/Humans.Onboarding.Tests/Architecture/OnboardingArchitectureTests.cs` enforces one thing: every `IStringLocalizer<T>` / `IHtmlLocalizer<T>` in the section names `OnboardingResource` or `SharedResource` (`OnboardingLocalizerBindingTests` is the per-key half of that guard — it checks every rendered key actually exists in the set its call site binds). The shape claims above are documentation, not assertions: a test that a section *lacks* something is forbidden by [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md).

## Issue queue

`Section` also declares this queue’s Area-label resource key and page-path aliases; Issues discovers both with the queue.

Onboarding owns the `Onboarding` issue queue, with no page-path aliases (the review queue is Consent's): it implements `IIssueQueueOwner` (Issues' `Contracts/`
folder) on its `Section` entry point, declaring the queue key and the roles that handle
issues filed against it — `ConsentCoordinator, ShiftsAdmin, UsersAdmin`, plus `Admin`, which handles every queue. Issues
discovers the declaration through DI and holds no list of sections; dropping the seam
sends this section's stored issues to the Admin-only queue.
