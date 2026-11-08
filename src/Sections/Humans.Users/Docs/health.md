# Users — target shape

## 1. What the section does

Users is the register of people. It knows who each person is (one account, the addresses
that prove it, how they sign in), what they have told us about themselves (the profile: names,
place, birthday without a year, picture, languages, volunteer history, medical
needs (food answers moved to Cantina), contact handles with a visibility each), which kinds of mail they will accept, whether
they were at the event in a given year, and where their account is in its life: bare,
active, suspended, rejected, deleting, deleted, or folded into another account.

Every other section asks Users one of a few questions — who is this id, which id owns this
address, who is on site, who is due for deletion — and gets back a snapshot that is answered
from memory, because the whole register fits in memory and is kept there.

People manage themselves under `/Profile/Me/*`; members see each other under `/Profile/{id}`,
the birthday calendar and the map under `/Users/*`; member admins manage people under
`/Users/Admin/*`; one repair screen sits at `/Profile/Admin/EmailProblems`. `/Email/Unsubscribe/*`
and the guest data pages (people with no session or no profile) are Email's and Gdpr's.

## 2. The shapes

| Shape | Question it answers | Where it lives today |
|---|---|---|
| **Resolve a person** | id → snapshot; ids → snapshots; everyone; text → ranked matches; address → snapshot | `IUserService` over the cached `UserInfo` dictionary |
| **Address ownership** | which rows hold address X (`FindByAddressAsync`: aliased or exact, verified or any — callers pick the cardinality; prefix+suffix for the dev seeder); which addresses does account Y hold (all / verified / primary / Google / nobodies.team) | `IUserEmailService` over the cached snapshot; `IUserRepository.GetUserEmailsByAddressAsync` behind the write paths |
| **Address mutation** | add, verify, promote, demote, hide, delete, link/unlink a provider, reconcile the OAuth claim | `IUserEmailService` writes → `IUserManagement` storage commands → repository |
| **Profile mutation** | save the edit form; save medical conditions; set picture; set tier; stub a profile; replace languages / history | `IProfileEditorService` (validation + file) → `IUserManagement` (row) → repository |
| **Contact handles** | what can this viewer see of that owner; save the owner's list | `IContactFieldService` |
| **Mail consent** | Email owns preference reads, writes, merge, export and erasure | `ICommunicationPreferenceService` |
| **Presence** | Tickets owns participation and its export/retention paths | `ITicketService.GetParticipationsAsync` |
| **Account life** | create (OAuth callback, magic link, import), suspend / unsuspend / reject / restore, request and cancel deletion, anonymize on expiry, purge (non-prod), merge two accounts | `IExternalLoginService`, `IAccountProvisioningService`, `IHumanLifecycleService`, `IAccountDeletionService`, `IAccountMergeService` + `IDuplicateAccountService`, `INonCompliantMemberSuspension` |
| **Cache signal** | "this person changed" from a write the service did not see | section-internal: `IUserInfoSliceRefresher` + `UserInfoSaveChangesInterceptor` |
| **Repair** | email-invariant violations; the one remaining data migration (legacy Identity emails) | `IEmailProblemsService` behind `/Profile/Admin/EmailProblems`; `ISectionDataMigrations` on `Section.cs` |
| **Admin views** | list / detail / audience; suspend, unsuspend, reject, purge; merge requests | `UsersAdminController`, `UsersAdminAccountMergesController` |
| **Directory views** | whose birthday is this month; where everyone is | `UsersController` (`/Users/Birthdays`, `/Users/Map`) |

Recurring jobs: nightly deletion expiry, nightly non-compliance suspension.

## 3. Structure

The shapes imply one register with one cache and one write funnel:

- **One read surface**, `IUserService`, answering *resolve a person* from the cache. Every
  other section reads through it and nothing else.
- **One write funnel** owning every row write to the tables the section owns, so the cache
  refresh happens exactly once per write: `IUserWrite` is the write half other sections call,
  `IUserManagement : IUserService, IUserWrite` adds the section-only half; one `CachingUserService`
  behind both. Domain services (`UserEmailService`,
  `ProfileEditorService`, `ContactFieldService`) hold the
  rules and call the funnel; they do not reach the repository around it — except where a table
  is that service's own lane (contact fields).
- **Address ownership as one lookup**: `FindByAddressAsync` filters the cached snapshot
  (`UserInfo.UserEmails`) by address form (aliased / exact) and verification, and returns
  every matching row; the caller picks the cardinality (first owner, distinct owners, exactly
  one, any other user). The write paths read the table through the one repository address
  read, `GetUserEmailsByAddressAsync`, and apply the same predicates in memory.
- **Account life**: `HumanLifecycleService`, `AccountDeletionService` and
  `ExternalLoginService` are orchestrators — no tables, no repository; they call the funnel and
  other sections' leaves. `AccountMergeService` is a section service over
  `account_merge_requests` through its own repository. It and `AccountProvisioningService` also
  reach `IUserRepository` and `UserManager<User>` around the funnel (the pending-email settle,
  the contact-source stamp, account create); the target is that those writes go through the
  funnel too. Merge is an ordered fan-out over `IUserMerge` with the tombstone last.
- **Controllers translate only.** The `/Profile` surface is one controller per shape:
  `ProfileController` (own profile), `ProfileEmailsController` (own and admin email grids)
  and `ProfileViewController` (others' profiles, picture, popovers). People search is the
  `/api/profiles/search` endpoint; the search page is Search's.
- **Repair is temporary**: the email-problems screen and the data migration each retire when
  production reads zero.

## 4. Invariants

- The full app is reachable only when `User.State == Active`; state is written at each
  transition and never derived on read — `src/Humans.Web/Authorization/MembershipRequiredFilter.cs:70`.
- A verified account holds exactly one verified `IsPrimary` address — `EnsurePrimaryInvariantAsync`,
  `src/Sections/Humans.Users/Services/UserEmailMutations.cs:209`. A verified address belongs to at
  most one account: verifying an address another account holds verified opens a merge request
  instead — `src/Sections/Humans.Users/Services/UserEmailService.cs:127`. `user_emails.IsGoogle`
  is dropped; the Workspace identity is GoogleIntegration's.
- An address is rewritten by exactly one path, the OAuth reconcile matched on
  `(Provider, ProviderKey)` — `src/Humans.Analyzers/EmailMutationPathsAnalyzer.cs`.
- Birthday carries month and day only; the year is stored as 4 —
  `src/Sections/Humans.Users/Services/UserProfileMutations.cs:130`.
- `MedicalConditions` is Art. 9 data: the owner edits it on `/Profile/Me/Medical`, and anyone else
  sees it only behind `ShiftsPolicies.MedicalDataViewer` — `src/Sections/Humans.Users/Controllers/ProfileController.cs:317`.
- A merge leaves the source row as a tombstone (`MergedToUserId`, far-future lockout) and never
  deletes it — `src/Sections/Humans.Users/Data/Repositories/UserRepository.cs:175`,
  `src/Sections/Humans.Users/Data/Repositories/UserRepository.cs:205`. The merge is ordered, not
  transactional, and each `IUserMerge.ReassignAsync` must be idempotent (a contract on every
  implementer; no test pins it).
- `CachingUserService` and `IUserInfoSliceRefresher` are the same singleton —
  `src/Sections/Humans.Users/Section.cs:112`.
- Deletion is a 30-day grace period — `src/Sections/Humans.Users/Services/AccountDeletionService.cs:52`;
  purge never runs in Production and never on oneself —
  `src/Sections/Humans.Users/Services/AccountDeletionService.cs:157`,
  `src/Sections/Humans.Users/Services/AccountDeletionService.cs:166`.
- Contact-field visibility resolves from the viewer's relation to the owner: self and Board see
  all, coordinators see `CoordinatorsAndBoard` and below, shared-team members see `MyTeams` and
  below, other active members see `AllActiveProfiles` only — `src/Sections/Humans.Users/Helpers/ContactFieldVisibilityRules.cs:10`.
- Nobody opens a suspended member's profile page — `src/Sections/Humans.Users/Controllers/ProfileViewController.cs:58`;
  the popover still shows a suspended member's basic identity with a suspended badge.

## 5. Seams

- Drop of the shadow `users.GoogleEmail` and `user_emails` `IsGoogle` / `GoogleEmailStatus`
  columns waits on prod verification (peterdrier/Humans#2156).
- `Profile.BurnerName/FirstName/LastName` remain dual-written and read by current consumers;
  retiring those columns requires migrating those readers first (nobodies-collective/Humans#1098).
- Users' one remaining data migration, `users.legacy-identity-emails`, retires when production
  reports it done.
- `AccountMergeRequest` still carries `User` navs; strip when the nav-strip pattern is
  generalised.
- The partial unique index on `user_emails.Email` (verified rows) is a unique index on an
  editable string, which [`unique-constraints-ids-only`](../../../../memory/architecture/unique-constraints-ids-only.md)
  forbids; the service check above is the contract and the drop is schema work (`Docs/debt.yml`).
- Analyzer enforcement of the read/write split is advisory today.

## 6. Deliberately not done

- No `TransactionScope` around the merge — cross-section DB transactions are disallowed.
- No DB unique index on `IsPrimary` — the service is the contract
  (`memory/architecture/db-enforcement-minimal.md`).
- No `SurfaceBudget` on `IUserService` while the Users+Profiles merge settles.
- No custom repository over the Identity tables — `UserManager` owns them.
- No tests asserting absences (stripped navs, banned method-name tokens).
- No separate profile cache — `UserInfo` is the one read model.

## Load-bearing weirdness

- `User.Email` is an override computed from `UserEmails` and silently falls back to the legacy
  Identity column when the collection is not loaded. Callers that want truth read `UserInfo`.
- The inner `UserService` is keyed-scoped and the decorator opens a scope per call; several
  read methods exist only on the decorator and the inner throws `NotSupportedException`.
- `UserInfoSaveChangesInterceptor` is registered on every section's context because Identity
  writes can happen anywhere.
- `IUserEmailService` lazily resolves `ITicketService` through `IServiceProvider` to break a
  DI cycle.
- `ExternalLoginService` keeps the `Humans.Application.Services.Users` namespace because an
  analyzer names it by full name.
- Identity's `UserManager` / `SignInManager` are Users-only: `AccountController` (Shell), Development
  and Tickets sign in through `IUserSignInService` (CENTRAL-106 closed).
- `Views/Profile/Edit.cshtml` localizes its shift-preference strings (`Shifts_EditPreferences`,
  `Shifts_PreferencesHelp`) through `ShiftsResource` on purpose.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-06 | First doctoring | peterdrier/Humans#1603 |
| 2 | 2026-11-06 | Target and docs match the code; the dead UsersAdminOnly policy is gone | peterdrier/Humans#2339 |
