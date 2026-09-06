# Users — target shape

## 1. What the section does

Users is the register of people. It knows who each person is (one account, the addresses
that prove it, how they sign in), what they have told us about themselves (the profile: names,
place, birthday without a year, picture, languages, volunteer history, dietary and medical
needs, contact handles with a visibility each), which kinds of mail they will accept, whether
they were at the event in a given year, and where their account is in its life: bare,
active, suspended, rejected, deleting, deleted, or folded into another account.

Every other section asks Users one of a few questions — who is this id, which id owns this
address, who is on site, who is due for deletion — and gets back a snapshot that is answered
from memory, because the whole register fits in memory and is kept there.

People manage themselves under `/Profile/Me/*`; member admins manage people under
`/Users/Admin/*`; a handful of one-off repair screens sit under `/Profile/Admin/*`;
`/Unsubscribe/*` and `/Guest/*` serve people with no session or no profile.

## 2. The shapes

| Shape | Question it answers | Where it lives today |
|---|---|---|
| **Resolve a person** | id → snapshot; ids → snapshots; everyone; text → ranked matches; address → snapshot | `IUserServiceRead` over the cached `UserInfo` dictionary |
| **Address ownership** | which account holds address X (verified / any / other-than-me / prefix+suffix); which addresses does account Y hold (all / verified / primary / Google / nobodies.team) | `IUserEmailService` lookups, `IUserRepository.UserEmails` |
| **Address mutation** | add, verify, promote, demote, hide, delete, link/unlink a provider, reconcile the OAuth claim | `IUserEmailService` writes → `IUserService` storage commands → repository |
| **Profile mutation** | save the edit form; save dietary/medical; set picture; set tier; set IBAN; stub a profile; replace languages / history | `IProfileEditorService` (validation + file) → `IUserService` (row) → repository |
| **Contact handles** | what can this viewer see of that owner; save the owner's list | `IContactFieldService` |
| **Mail consent** | is Y opted out of category C; flip it; mint / validate an unsubscribe token | `ICommunicationPreferenceService`, `IUnsubscribeService`, `IUnsubscribeTokenProvider` |
| **Presence** | for year N: declare not attending, undo, ticket-sync upsert/remove, admin backfill; who was on site | `IUserService` participation methods, `IUserServiceRead.GetOnsiteUsersAsync`, `IUserParticipationBackfillService` |
| **Account life** | create (OAuth callback, magic link, import), suspend / unsuspend / reject / restore, request and cancel deletion, anonymize on expiry, purge (non-prod), merge two accounts | `IExternalLoginService`, `IAccountProvisioningService`, `IHumanLifecycleService`, `IAccountDeletionService`, `IAccountMergeService` + `IDuplicateAccountService`, `INonCompliantMemberSuspension` |
| **Cache signal** | "this person changed" from a write the service did not see | `IUserInfoInvalidator`, `IUserInfoSliceRefresher` + `UserInfoSaveChangesInterceptor` |
| **Repair** | email-invariant violations; profile-less accounts; unsynced names; picture migration | `IEmailProblemsService`, the `/Profile/Admin/*` backfill controllers |
| **Admin views** | list / detail / roles roster / audience / debug grid / merges | `UsersAdminController`, `UsersAdminAccountMergesController`, `UsersAdminDebugController` |

Recurring jobs: nightly deletion expiry, nightly non-compliance suspension.

## 3. Structure

The shapes imply one register with one cache and one write funnel:

- **One read surface**, `IUserServiceRead`, answering *resolve a person* from the cache. Every
  other section reads through it and nothing else.
- **One write funnel**, `IUserService`, owning every row write to the tables the section owns,
  so the cache refresh happens exactly once per write. Domain services (`UserEmailService`,
  `ProfileEditorService`, `ContactFieldService`, `CommunicationPreferenceService`) hold the
  rules and call the funnel; they do not reach the repository around it — except where a table
  is that service's own lane (contact fields, communication preferences).
- **Address ownership as one lookup family**: each lookup is a filter on the cached snapshot
  (`UserInfo.UserEmails`), not a query. Variants differ by predicate (verified? primary?
  Google? nobodies.team?) and by cardinality (one id, one address, a set). The ideal is a
  handful of methods over a predicate, not one method per predicate × cardinality.
- **Account life**: `HumanLifecycleService`, `AccountDeletionService` and
  `ExternalLoginService` are orchestrators — no tables, no repository; they call the funnel and
  other sections' leaves. `AccountMergeService` is a section service over
  `account_merge_requests` through its own repository. It and `AccountProvisioningService` also
  reach `IUserRepository` and `UserManager<User>` around the funnel (the pending-email settle,
  the contact-source stamp, account create); the target is that those writes go through the
  funnel too. Merge is an ordered fan-out over `IUserMerge` with the tombstone last.
- **Controllers translate only.** `ProfileController` is one controller over unrelated
  shapes (own profile, own addresses, others' profiles + messaging); the target is a
  controller per shape.
- **Repair screens are temporary** and sit under an admin nav group named Temp; each retires
  when its count reads zero in production.

## 4. Invariants

- The full app is reachable only when `User.State == Active`; state is written at each
  transition and never derived on read.
- Exactly one verified `IsPrimary` address per account; at most one `IsGoogle`; a verified
  address belongs to at most one account. Each is service-enforced: the verify paths
  open a merge request when another account already holds the address verified, and the OAuth
  reconcile blocks or displaces before it writes.
- An address is rewritten by exactly one path: the OAuth reconcile, matched on
  `(Provider, ProviderKey)`.
- `Attended` is permanent; a `NotAttending` self-declaration is undone only by its author.
- Birthday carries month and day only.
- `MedicalConditions` is Art. 9 data and is gated at every render by `MedicalDataViewer`.
- A merge is ordered, not transactional; every `IUserMerge.ReassignAsync` is idempotent; the
  source row becomes a tombstone (`MergedToUserId`, `MergedAt`, far-future lockout) and is never
  deleted; tombstones cannot sign in.
- `CachingUserService` and `IUserInfoInvalidator` are the same singleton.
- Deletion is a 30-day grace period; memberships and roles go on request, data goes on expiry;
  purge never runs in Production and never on oneself.
- Unsubscribe endpoints are unauthenticated and never enumerate accounts.
- Contact-field and email visibility resolve from the viewer's relation to the owner: self and
  Board see all, coordinators see `CoordinatorsAndBoard` and below, shared-team members see
  `MyTeams` and below, other active members see `AllActiveProfiles` only.
- Non-active humans cannot view other profiles or send messages; regular humans cannot view
  suspended profiles.

## 5. Seams

- Drop of the shadow `users.GoogleEmail`, `users.GoogleEmailStatus`, and
  `user_emails.IsNotificationTarget`-named columns waits on prod verification.
- Retiring `Profile.BurnerName/FirstName/LastName` once `/Profile/Admin/NameBackfill` reads 0
  in production (nobodies-collective/Humans#1098).
- The Temp backfill screens retire on the same signal.
- `AccountMergeRequest` still carries `User` navs; strip when the nav-strip pattern is
  generalised.
- `PolicyNames.HumanAdminOnly` is registered with no call site.
- The partial unique index on `user_emails.Email` (verified rows) is a unique index on an
  editable string, which [`unique-constraints-ids-only`](../../../../memory/architecture/unique-constraints-ids-only.md)
  forbids; the service check above is the contract and the drop is schema work (`Docs/debt.yml`).
- Analyzer enforcement of the read/write split is advisory today.

## 6. Deliberately not done

- No `TransactionScope` around the merge — cross-section DB transactions are disallowed.
- No DB unique index on `IsPrimary` — the service is the contract
  (`memory/architecture/db-enforcement-minimal.md`).
- No `SurfaceBudget` on `IUserService` while the Users+Profiles merge settles.
- No dietary-preference enum — legacy free text stays readable.
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
- `IUserEmailService` lazily resolves `ITicketServiceRead` through `IServiceProvider` to break a
  DI cycle.
- `ExternalLoginService` keeps the `Humans.Application.Services.Users` namespace because an
  analyzer names it by full name.
- `AccountController` (Shell) and the Development seeders inject `UserManager` directly — the
  §2a exception.
- `Views/Profile/Edit.cshtml` localizes its shift-preference strings (`Shifts_EditPreferences`,
  `Shifts_PreferencesHelp`) through `ShiftsResource` on purpose.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-06 | First doctoring | peterdrier/Humans#1603 |
