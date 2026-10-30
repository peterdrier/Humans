<!-- freshness:triggers
  src/Sections/Humans.Governance/**
  src/Sections/Humans.Gdpr/**
  src/Sections/Humans.Gdpr/Contracts/**
  src/Sections/Humans.Users/Services/AccountDeletionService.cs
  src/Sections/Humans.Users/Services/ProfileService.cs
  src/Sections/Humans.Users/Services/UserService.cs
  src/Sections/Humans.Consent/Services/ConsentService.cs
  src/Sections/Humans.Teams/**
  src/Sections/Humans.Auth/Services/RoleAssignmentService.cs
  src/Sections/Humans.Shifts/Services/ShiftSignupService.cs
  src/Sections/Humans.Feedback/Services/FeedbackService.cs
  src/Sections/Humans.Notifications/Services/NotificationInboxService.cs
  src/Sections/Humans.Tickets/Services/TicketQueryService.cs
  src/Sections/Humans.Campaigns/Services/CampaignService.cs
  src/Sections/Humans.Camps/Services/**
  src/Sections/Humans.AuditLog/Services/**
  src/Sections/Humans.Budget/Services/BudgetService.cs
  src/Sections/Humans.Users/Services/AccountMergeService.cs
  src/Sections/Humans.Surveys/Services/SurveyService.cs
  src/Sections/Humans.Agent/Services/AgentService.cs
  src/Sections/Humans.Events/Services/Service.cs
  src/Sections/Humans.Issues/Services/IssuesService.cs
  src/Sections/Humans.Expenses/Services/ExpenseReportService.cs
  src/Sections/Humans.Finance/Services/Service.cs
  src/Sections/Humans.Gate/Services/GateService.cs
  src/Sections/Humans.GoogleIntegration/Services/GoogleSyncLogService.cs
  src/Sections/Humans.MailerLite/Services/MailerLiteGdprContributor.cs
  src/Sections/Humans.Email/Services/EmailOutboxService.cs
  src/Sections/Humans.Backdoor/Services/BackdoorApiKeyService.cs
  src/Sections/Humans.Rideshare/Services/**
  src/Sections/Humans.Workgroups/Services/**
  src/Sections/Humans.Preapprovals/Services/PreapprovalService.Gdpr.cs
  src/Sections/Humans.Grants/Services/GrantService.Gdpr.cs
  src/Sections/Humans.Calendar/Services/CalendarFeedTokenService.cs
-->
<!-- freshness:flag-on-change
  Contributor list, JSON section names/shapes, or fan-out orchestration may have shifted; per-section table must stay in sync with each contributor's slice.
-->

# GDPR Data Export

GDPR Article 15 gives every human the right to obtain a copy of all personal
data an organization holds about them. Humans satisfies this right through a
self-service download at `/Gdpr/DownloadData` (for humans who already
have a profile) and `/Gdpr/Guest/DownloadData` (for authenticated accounts that have
not yet completed onboarding). Both endpoints produce the same JSON document
shape.

## Architecture

The export is assembled by `IGdprService` (declared on `Humans.Gdpr.Contracts`, implemented by the internal `GdprService` in `Humans.Gdpr`), a
pure orchestrator that owns no database tables and has no `DbContext`
dependency. It injects `IEnumerable<IUserDataContributor>` and fans out one
call per contributor, merging the returned slices into a single document keyed
by section name. The same service also owns the Article 17 erasure fan-out
(`EraseForUserAsync`) over the same contributor roster — see
[`src/Sections/Humans.Gdpr/Docs/Gdpr.md`](../../../src/Sections/Humans.Gdpr/Docs/Gdpr.md).

Every section service that owns user-scoped tables implements
`IUserDataContributor`. When a new user-scoped section is added, its owning
service gains an `IUserDataContributor` implementation (and a DI registration)
and the export automatically includes it — the orchestrator never needs to
change.

```
┌─────────────────────────┐
│ MemberDataController /  │
│ GuestDataController     │
└────────────┬────────────┘
             │
             ▼  ExportForUserAsync(userId)
┌─────────────────────────────────────────────────┐
│             IGdprService                        │
│          (Humans.Gdpr section project)          │
│                                                 │
│   foreach contributor in IEnumerable<IUDC>      │
│       slices += contributor.ContributeForUser() │
│       slices += contributor                     │
│           .ContributeForIdentityChain()         │
│   return { ExportedAt, UserId,                  │
│            MergedFromUserIds, ...merged slices }│
└──────┬──────────────────────────────────────────┘
       │
       ▼  ContributeForUserAsync(userId), then
       ▼  ContributeForIdentityChainAsync(userId, allUserIds)
┌─────────────────────────────────────────────────┐
│  Every section service that owns user-scoped    │
│  tables, each implementing IUserDataContributor │
│  — the Contributor column of the table below is │
│  the roster of everything the export can hold.  │
└─────────────────────────────────────────────────┘
```

The contributor named in that column is the type **registered** as
`IUserDataContributor`, and whether that is a cached section's decorator or its inner
service is the registering section's own call: `CachingEventService`,
`CachingRideshareService`, `CachingWorkgroupService`, `CachingTeamService`, `CachingCampService`
and `CachingTicketQueryService` bind the decorator, while Consent and Auth have decorators and
bind the inner service. Whichever is registered
is the type that carries `ErasureDeclaration`.

**The Article 17 roster is one wider than this table.** `MailerLiteGdprContributor`
(`src/Sections/Humans.MailerLite/Services/MailerLiteGdprContributor.cs`) is registered as a
contributor and returns no slices, so it has no row and never appears in an export; its
erasure deletes the person's MailerLite subscriber outright, declared under its own
`MailerLiteSubscriber` key. Auditing the deletion fan-out means this table plus that one.

### Why sequential fan-out (not `Task.WhenAll`)

This was once a correctness requirement — every contributor read through one
scoped `HumansDbContext`, which is not thread-safe. That context is gone. Each
section owns its own `DbContext` type, so no two contributors touch the same
instance; how a given repository obtains its context varies — some take one by
injection, some open one per call through `IDbContextFactory<T>` — and neither
recreates the sharing. The hazard went with the shared context, and
`design-rules.md` §8a records the reason as obsolete.

Sequential is now a simplicity choice, and it stays: one contributor at a time
keeps failure attribution and log order plain, and at our small scale an export
completes well under a second, so parallelism would buy nothing measurable. The
loop in `GdprService.ExportForUserAsync` could be made parallel in place
without changing the contract — there is just no reason to.

## Section names

Each contributor declares its own section-name constants, kept beside the
class that uses them — there is no central registry. A section name is
whatever a contributor chooses for its slice; it must be unique across
contributors. The export format is not a spec — it changes as contributors
change, and nothing outside this codebase reads it — so renaming a key is an
ordinary change, not a breaking one.

## JSON output shape

The top-level document is an object with `ExportedAt` (invariant ISO-8601 UTC
instant string), `UserId`, `MergedFromUserIds`, plus one key per section
contributed. A single-object section whose entity does not exist for this user
is omitted (a `null` slice); a collection section with no rows appears as `[]`.

`UserId` is the account the export belongs to — the **surviving** account when
the request came in under an id that has since been merged away — and
`MergedFromUserIds` lists the archived ids folded into it (`[]` when there are
none). They are the key that makes the slices legible: sections deliberately keep
rows on the archived id (audit entries, consent records, assembly-vote rosters
and ballots), so without this header a row reading `UserId: 3` inside an export
for account 5 looks like somebody else's data.

The orchestrator passes the resolved survivor's ID to every export contributor,
so requests under either ID reach the same record without making crosscuts call
Users. A slice over rows the merge **left behind** still reads every ID in
`UserInfo.AllUserIds`, while a slice over columns the merge **moved** (Governance's
assembly-vote actor columns, reassigned by `ReassignAsync`) reads the survivor's
ID alone. Erasure keeps its per-ID input: Users separately calls it for each
archived account and the survivor.

| Section | Contributor | Shape |
|---------|-------------|-------|
| `Account` | `UserService` | Single object with user identity, display name, preferred language, lifecycle state, deletion request/scheduled/post-event-hold instants, created/last-login instants. |
| `LegacyEventParticipations` | `UserService` | Array of `{ UserId, Year, Status, Source, DeclaredAt, CheckedInAt }` — the frozen Users `event_participations` rows for every id in the merge chain (live record: the `EventParticipations` slice). Retained on erasure. |
| `UserEmails` | `UserService` | Array of `{ Email, IsVerified, IsOAuth, IsNotificationTarget, IsGoogle, GoogleEmailStatus, VerificationSentAt, CreatedAt, UpdatedAt, Visibility }` (`IsOAuth` is true when a provider is linked; `IsGoogle` / `GoogleEmailStatus` are the frozen legacy flags — the live record is the `WorkspaceIdentity` slice; key names are pinned for export stability). |
| `Profile` | `UserService` | Single object with burner name, legal name, birthday (month/day only), city/country, lat/lng, bio, pronouns, contribution interests, board notes, the legacy membership tier (live tier: the `MembershipTier` slice), the legacy approval and consent check state (live review: the `ConsentReview` slice), suspension state, emergency contact, medical conditions, the legacy food columns (meal preference, allergies, intolerances — still held until the `cantina.profile-dietary` retirement drops them), created/updated instants. |
| `MergedProfiles` | `UserService` | Array of `{ UserId, Profile }` for archived profiles in the transitive merge chain. Reuses the current profile shape, including retained dietary and medical data; requests under an archived ID resolve to the survivor first. |
| `ContactFields` | `UserService` | Array of `{ FieldType, Label, Value, Visibility }`. |
| `VolunteerHistory` | `UserService` | Array of `{ Date, EventName, Description, CreatedAt }`. |
| `Languages` | `UserService` | Array of `{ LanguageCode, Proficiency }`. |
| `LegacyCommunicationPreferences` | `UserService` | Array of `{ Category, OptedOut, InboxEnabled, SubscribedAt, UpdatedAt, UpdateSource }` — the frozen Users `communication_preferences` rows (live record: the `CommunicationPreferences` slice). Deleted on erasure. |
| `CommunicationPreferences` | `CommunicationPreferenceService` (Email) | Array of `{ Category, OptedOut, InboxEnabled, SubscribedAt, UpdatedAt, UpdateSource }` — the effective preferences, the legacy Users row answering until the `email.communication-preferences` copy or a write replaces it. Deleted on erasure. |
| `WorkspaceIdentity` | `WorkspaceIdentityService` (GoogleIntegration) | Single object `{ Email, Status }` — the member's effective Google Workspace identity (the legacy `UserEmails` flags answering until the `google.workspace-identities` copy runs); null when the member has no verified address. Erasure deletes the `google_workspace_identities` row. |
| `EventParticipations` | `EventParticipationService` (Tickets) | Array of `{ Year, Status, Source, DeclaredAt, CheckedInAt }` covering every event-year the user has a participation record for (Ticketed / Attended / NoShow / NotAttending), the legacy Users row answering until the `tickets.event-participations` copy has run; requests under an archived ID resolve to the survivor. Retained on erasure. |
| `Applications` | `ApplicationDecisionService` | Array of tier application records with `StateHistory` inline. |
| `MembershipTier` | `MembershipTierService` | Single object `{ Tier, GrantedAt, ExpiresAt, GrantedByApplicationId }`, or `{ Tier }` for a not-yet-copied legacy profile tier; null for a Volunteer. Retained on erasure (register of members). |
| `Consents` | `ConsentService` | Array of `{ DocumentName, DocumentVersion, ExplicitConsent, ConsentedAt, IpAddress, UserAgent }`. |
| `ConsentReminder` | `ConsentReminderService` | Array of `{ UserId, LastSentAt }` — when the re-consent reminder email last went to each id in the merge chain (survivor first); null when none. Erased in full. |
| `ConsentReview` | `ConsentReviewService` | Array of `{ UserId, Status, CheckedAt, Notes, IsApproved, RejectionReason, RejectedAt }` — the Consent Coordinator's review rows for every id in the merge chain (survivor first); null when none. Erasure keeps status, dates, approval and reviewer ids and erases the notes and the rejection reason. |
| `TeamMemberships` | `CachingTeamService` | Array of `{ TeamName, Role, JoinedAt, LeftAt, TeamRoles[] }`. |
| `TeamJoinRequests` | `CachingTeamService` | Array of `{ TeamName, Status, Message, ReviewNotes, RequestedAt, ResolvedAt, StateHistory[] }` (`StateHistory` entries are `{ Status, ChangedAt, Notes }`). |
| `TeamEarlyEntry` | `CachingTeamService` | Array of `{ TeamName, ProjectName, EntryDate, GrantedAt }` — the user's team early-entry grants. |
| `RoleAssignments` | `RoleAssignmentService` | Array of `{ RoleName, ValidFrom, ValidTo }`. |
| `ShiftSignups` | `ShiftSignupService` | Array of `{ EventName, Department, RotaName, DayOffset, IsAllDay, Status, Enrolled, StatusReason, CreatedAt, UpdatedAt, ReviewedAt }`. |
| `VolunteerEventProfiles` | `ShiftSignupService` | Array of `{ Skills, Quirks, Languages, CreatedAt, UpdatedAt }` per-event profile records (dietary data is exported in `DietaryProfile`, medical in `Profile`). |
| `GeneralAvailability` | `ShiftSignupService` | Array of `{ EventName, AvailableDayOffsets, UpdatedAt }`. |
| `ShiftTagPreferences` | `ShiftSignupService` | Array of `{ TagName }`. |
| `Events` | `CachingEventService` (delegates to `EventService`) | Single object with `{ SubmittedEvents: [{ Id, CampId, GuideSharedVenueId, CategoryId, Title, Description, LocationNote, Host, StartAt, DurationMinutes, IsRecurring, RecurrenceDays, PriorityRank, Status, SubmittedAt, LastUpdatedAt }], Favourites: [{ GuideEventId, DayOffset, CreatedAt }], Preference: { ExcludedCategorySlugs, UpdatedAt }, AdditionalPreferences: [{ UserId, ExcludedCategorySlugs, UpdatedAt }] }` — the events the human submitted, their favourites and category-exclusion preference; `Preference` is null when no preference row exists, and `AdditionalPreferences` holds preferences of archived accounts merged into this one. |
| `FeedbackReports` | `FeedbackService` | Array of feedback reports with nested `Messages[]`. |
| `FeedbackReplies` | `FeedbackService` | Array of `{ FeedbackReportId, Content, EmailEnqueuePending, CreatedAt }` — the user's replies on other people's reports. |
| `Issues` | `IssuesService` | Array of `{ Title, Description, Category, Section, Status, PageUrl, UserAgent, AdditionalContext, CreatedAt, ResolvedAt, Comments: [{ Content, IsFromUser, CreatedAt }] }` — issues filed by the user including their comments. |
| `IssueComments` | `IssuesService` | Array of `{ IssueId, Content, CreatedAt }` — the user's comments on other people's issues. |
| `Notifications` | `NotificationInboxService` | Array of `{ Title, Body, ActionUrl, Priority, Source, CreatedAt, ReadAt, ResolvedAt }`. |
| `TicketOrders` | `CachingTicketQueryService` | Array of `{ BuyerName, BuyerEmail, TotalAmount, Currency, PaymentStatus, DiscountCode, PurchasedAt }`. |
| `TicketAttendeeMatches` | `CachingTicketQueryService` | Array of `{ AttendeeName, AttendeeEmail, TicketTypeName, Price, Status }`. |
| `CampaignGrants` | `CampaignService` | Array of `{ CampaignTitle, Code, AssignedAt, RedeemedAt, EmailStatus }`. |
| `CampRoleAssignments` | `CachingCampService` | Array of `{ CampSlug, SeasonYear, RoleName, AssignedAt, AssignedByUserId }`. |
| `CampMemberships` | `CachingCampService` | Array of `{ Id, CampSeasonId, CampSlug, SeasonYear, Status, RequestedAt, ConfirmedAt, ConfirmedByUserId, RemovedAt, RemovedByUserId, HasEarlyEntry }` — the human's camp member rows. Erased in full. |
| `AccountMergeRequests` | `AccountMergeService` | Array of `{ Status, Role, CreatedAt, ResolvedAt }` (Role is "Target" or "Source"), covering every account in the resolved merge chain without duplicate requests. |
| `AuditLog` | `AuditLogService` | Array of `{ Action, EntityType, OccurredAt, Role }` (Role is "Actor" or "Subject"). |
| `BudgetAuditLog` | `BudgetService` | Array of `{ EntityType, FieldName, Description, OccurredAt }`. |
| `AgentConversations` | `AgentService` | Array of `{ Id, StartedAt, LastMessageAt, Locale, MessageCount, Messages: [{ Role, Content, CreatedAt, Model, RefusalReason, HandedOffToFeedbackId }] }` — the user's AI assistant conversations with full message history. |
| `ExpenseReports` | `ExpenseReportService` | Array of `{ Id, Status, Note, PayeeName, PayeeIban (masked), Total, MaxAmount, SubmittedAt, CoordinatorEndorsedAt, ApprovedAt, LastRejectionReason, LastRejectedAt, CreatedAt, Lines: [{ Id, Description, Amount, LineType, ParentLineId, SortOrder, Declined, DeclinedReason, DeclinedAt, Attachment? }] }` — the user's expense reports including line items and attachment metadata; null when the user has no reports. |
| `Preapprovals` | `PreapprovalService` | Array of `{ Program, Label, MaxAmount, Status, ApplicationNote, RequestedAmount, DecidedAt, ExpenseReportId, CreatedAt }` — the human's own pre-approval grants. Decided grants are retained after erasure (fiscal basis); pending applications are deleted. |
| `PreapprovalDecisions` | `PreapprovalService` | Single object `{ ProgramsCreated[], ManagerOf[], ManagersAdded[], GrantsDecided[] }` — programs the human created, managed or added managers to, and grants they decided; null when they did none of these. Retained; manager rows are deleted. |
| `Grants` | `GrantService` | Array of `{ Program, Year, Entitles, SourceRef, ExpiresOn, IssuedAt, NotifiedAt, RevokedAt, RevokeReason }` — the human's own grants, any state. Deleted on erasure. |
| `GrantDecisions` | `GrantService` | Single object `{ ProgramsCreated[], ManagerOf[], ManagersNamed[], GrantsIssued[], GrantsRevoked[] }` — programs the human created or manages, managers they named (`{ Program, AddedAt }`), and grants they issued or revoked; null when none. Retained as the record of who decided (bare id, no longer resolves to a person once anonymized); manager rows are deleted. |
| `ExpenseAuditLog` | `ExpenseReportService` | Single object `{ Entries: [{ Action, EntityType, EntityId, Description, OccurredAt }] }` covering all expense-related audit events (submit, endorse, approve, reject, IBAN set/remove/reveal, etc.); null when the user has no expense audit entries. |
| `PaymentDetails` | `Finance.PaymentDetailsService` | Single object `{ Iban (masked) }` — the bank account the member is reimbursed to; null when none is on record. Erased in full. |
| `DietaryProfile` | `Cantina.DietaryProfileService` | Single object `{ DietaryPreference, Allergies, AllergyOtherText, Intolerances, IntoleranceOtherText }` — the member's food answers (falls back to the legacy profile columns until copied); null when none. Never carries medical conditions. Erased in full. |
| `HoldedCreditorAccount` | `Finance.Service` | Single object `{ SupplierAccountNum, HoldedContactId, Source }` — the requested account's Holded creditor account binding; null when no binding exists. |
| `AdditionalHoldedCreditorAccounts` | `Finance.Service` | Array of `{ UserId, SupplierAccountNum, HoldedContactId, Source }` — bindings held by archived accounts merged into this one; null when there are none. Erased in full. |
| `SepaPayouts` | `Finance.Service` | Array of `{ GeneratedAt, FileName, SupplierAccountNum, HoldedContactId, CreditorName, Iban (masked), Amount, BookedAt, HoldedBankMovementId, ReconciledAt, WithdrawnAt }` — every SEPA credit transfer paid to the user, oldest first; empty when they have never been paid. Retained after erasure on the fiscal basis. |
| `CreditorLedger` | `Finance.Service` | Array of `{ AccountNum, Date, EntryNumber, Line, Type, Description, Debit, Credit, RunningBalance }` — the mirrored Holded journal lines of the member's creditor account(s); null when there are none, and withheld for an account another member is also bound to until an admin resolves the collision. Retained after erasure on the fiscal basis (the lines carry no user id, so they stop being linkable once the binding is erased). |
| `SurveyResponses` | `SurveyService` | Array of `{ Survey, SubmittedAt, Culture, Answers[] }` where each answer has `{ Question, SelectedLabels, GridSelections, GridSelectionLabels, RankedBallot, RankedBallotLabels, TextValue, RatingValue }`. |
| `AuthoredSurveys` | `SurveyService` | Array of `{ Survey, Status, RejectionNote, CreatedAt, QuestionCount }` — surveys the human wrote, Drafts nobody else can see included, since authoring is open to any approved human. Retained after erasure with the authorship link dropped and the rejection note deleted. |
| `SurveyInvitations` | `SurveyService` | Array of `{ Survey, InvitedAt, SentAt, EmailStatus, ReminderSentAt, Started, Completed }` — the invitation ledger, oldest first. Most responses are not identified, so for someone who was only invited, or who answered under completion tracking, this is the only survey record of theirs. Erased in full: the rows are deleted. |
| `GateScans` | `GateService` | Array of `{ OccurredAt, Verdict, Role, LaneId }` — the user's own gate activity, as guest or as scanner (`Role` is "Guest" or "Scanner"). Data-minimized: no barcode, no other person's identifiers. |
| `GoogleSyncLog` | `GoogleSyncLogService` | Array of `{ Action, OccurredAt, Description, ResourceName, UserEmail, Role, Source, Success, ErrorMessage }` — every Workspace sync row attributed to the human, merge tombstones followed. |
| `EmailOutbox` | `EmailOutboxService` | Array of `{ RecipientEmail, RecipientName, Subject, HtmlBody, TemplateName, Status, CreatedAt, SentAt }` — the same per-user outbox history the human reads at `/Email/Outbox`. |
| `EmailOutboxMergedAccounts` | `EmailOutboxService` | Same shape as `EmailOutbox` — rows still keyed to archived accounts merged into this one (merges before peterdrier/Humans#1999), read from the chain Gdpr passes in; only present for a merged account. Erased in full. |
| `BackdoorApiKeys` | `BackdoorApiKeyService` | Array of `{ Label, DisplayPrefix, CreatedAt, LastUsedAt, RevokedAt }` — the machine-API keys allocated to the human; null when they hold none. The stored hash is never exported: it is the credential itself. |
| `RideshareTrips` | `CachingRideshareService` | Array of `{ Id, Year, EventSettingsId, Direction, MemberPlaceLabel, MemberLatitude, MemberLongitude, Waypoints[], DepartureDate, ExpectedDurationDays, OvernightPlan, VehicleType, SeatsOffered, LuggageCapacity, CapacityNote, Restrictions, WillingToDetour, CostSharing, CostNote, LinkedTripId, Status, CreatedAt, UpdatedAt }` — the human's ride offers, oldest first. |
| `RideshareRequests` | `CachingRideshareService` | Array of `{ Id, Year, EventSettingsId, Direction, PickupPlaceLabel, PickupLatitude, PickupLongitude, DesiredDate, PartySize, LuggageLoad, CanContributeToFuel, Notes, Status, CreatedAt, UpdatedAt }` — the human's ride requests, oldest first. |
| `RideshareInterests` | `CachingRideshareService` | Array of `{ Id, TripId, RequestId, Seats, Message, Status, CreatedAt, RespondedAt }` — interests the human expressed (as rider, or as driver answering a request), oldest first. |
| `WorkgroupApplications` | `CachingWorkgroupService` | Array of `{ Workgroup, Status, Purpose, AppliedAt, RegisteredAt }` — the groups the human proposed. Retained after erasure with the applicant attribution dropped: a registered group outlives whoever proposed it. |
| `WorkgroupMemberships` | `CachingWorkgroupService` | Array of `{ Workgroup, Role, Pending, JoinedAt, LeftAt }` — pending join requests included. Erased in full. |
| `WorkgroupLogEntries` | `CachingWorkgroupService` | Array of `{ Workgroup, Kind, OccurredOn, Title, Body, CreatedAt }` — the group's written record of how it worked. Retained, authorship dropped. |
| `CalendarFeedToken` | `CalendarFeedTokenService` | `{ HasFeed }` — whether the human has ever minted a personal iCal feed. The token itself is never exported: it is a live credential and an export file gets forwarded; the human reads their URL off `/Calendar`, the one place it is shown. Erased in full: the row is deleted. |
| `WorkgroupMeetings` | `CachingWorkgroupService` | Array of `{ Workgroup, Title, StartUtc, EndUtc, Location, IsPublic, Minutes, CreatedAt }`. Retained, creator attribution dropped. |
| `WorkgroupDocuments` | `CachingWorkgroupService` | Array of `{ Workgroup, Title, Kind, Status, Authored, Edited, DispositionRecorded, CreatedAt, UpdatedAt }` — the three booleans say which attribution this row carries for this person. Retained, attributions dropped. |
| `WorkgroupComments` | `CachingWorkgroupService` | Array of `{ Workgroup, Document, Category, Body, Authored, Responded, HiddenByThisPerson, Disposition, Response, Hidden, HiddenReason, CreatedAt }`. Retained, author attribution dropped: the record of what was heard and decided against. |
| `AssemblyVotes` | `AssemblyVoteService` | Array of `{ Vote, Status, ClosesAt, Entitlement ("Official"/"Indicative"), Tier, IsBoardMember, Ballot: { Choice, Ranking[], Revision, CastAt, UpdatedAt, History: [{ Revision, Choice, Ranking[], RecordedAt }] }? }` — every assembly vote the human was on the roster for, with their current ballot and each revision of it; `Ballot` is null when they did not vote. Retained after erasure, unlinked: the vote is the association's record of a decision. |
| `AssemblyVoteActions` | `AssemblyVoteService` | Single object `{ RanVotes: [{ Vote, Status, Roles[] ("Drafted"/"Opened"/"Closed"), CreatedAt, OpenedAt, ClosedAt }], Peeks: [{ Vote, PeekedAt }] }` — the votes the human ran and the live tallies they peeked at. Declared as retained: the acta names the closer and the results page publishes the early-view list. |

All instants are serialized as invariant ISO-8601 strings (e.g.
`2026-04-15T10:30:00Z`) via `NodaTime` extensions.

## Extending the export

Adding a new section:

1. Declare the section-name constant on the owning contributor class itself
   (a `const string`, or `internal const string` where the type stays
   internal).
2. Make the owning service implement `IUserDataContributor`. Return a
   `UserDataSlice(sectionName, data)` with shape documented in a new table row
   above. **Null semantics:** for collection sections, always return the shaped
   collection (an empty list when the user has no records) — a collection key
   is always present as `[]`, never omitted. Return `null` data
   only for single-object sections whose underlying entity doesn't exist for
   this user (for example, a profileless account has no `Profile`). The
   orchestrator drops only `null` slices from the export, and logs an error
   and continues if a returned section name isn't also a key of that same
   contributor's `ErasureDeclaration`.
3. Register the forwarding factory in the owning section's own
   `Section.Register` — it belongs beside the rest of that section's DI setup,
   not in a shared registration file:

   ```csharp
   services.AddScoped<MyNewService>();
   services.AddScoped<IMyNewService>(sp => sp.GetRequiredService<MyNewService>());
   services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<MyNewService>());
   ```

`GdprExportDependencyInjectionTests` discovers contributors by reflection over
`Humans.Web` (the host assembly, successor to the deleted `Humans.Infrastructure`)
plus every section assembly via `SectionDiscoveryExtensions` — no type list to
update — and fails the build if a discovered contributor isn't wired in DI, so
the export can't silently drop a category.

## Right to deletion (Article 17)

Erasure runs through the same fan-out, over the same interface
(nobodies-collective/Humans#853). `IUserDataContributor` carries two Article 17
members alongside `ContributeForUserAsync`, so a section cannot export a
category without accounting for its deletion:

- `ErasureDeclaration` — a **static** table, one entry per section-name key
  the contributor owns (export keys plus any erasure-only key, e.g.
  MailerLite's). `null` means erased or anonymized in full; a string names
  what survives and the lawful basis for keeping it. It must not touch
  instance state, the DbContext or the clock: the architecture test reads it
  from an uninitialized instance. `GdprService.ExportForUserAsync` logs an
  error and continues if a contributor exports a key this table doesn't
  cover.
- `EraseForUserAsync(userId, ct)` — idempotent, because the job retries the
  whole cascade the next day after a mid-cascade failure.
- `ErasesLast` — `true` for the one contributor that owns the person's
  identity/account record; defaults to `false`. `GdprService.EraseForUserAsync`
  orders erasure by this flag, not by naming a specific contributor.

`IAccountDeletionService`
(`src/Sections/Humans.Users/Services/AccountDeletionService.cs`) still owns the
30-day grace period — on request it revokes team memberships and governance
roles immediately — but once the grace period expires the daily
`ProcessAccountDeletionsJob` runs the fan-out rather than a hand-wired cascade.
Contributors run sequentially — the same simplicity choice as the export, and
for the same reason (see "Why sequential fan-out" above); the contributor with
`ErasesLast` runs last so sections that still need the human's addresses can
resolve them, and a contributor that throws aborts the run with the deletion
markers still set. Erasure also reaches
the external processors that hold the human: it suspends their `@nobodies.team`
Workspace account before dropping the Google sync-log rows, and deletes their
MailerLite subscriber. Both paths keep the address out of anything that
survives — the Workspace suspend audits by actor id, not by address, because
`AuditLogService.EraseForUserAsync` deliberately keeps the append-only log. The
courtesy confirmation the job mails afterwards is sent with
`EmailMessage.DoNotPersist`, so it writes no outbox row: it goes out after the
collapse, so its `UserId` would resolve to null and the row would sit beyond the
reach of both `EmailOutboxService.EraseForUserAsync` and the retention sweep.
See `docs/guide/YourData.md` for the user-facing flow.

The admin-initiated purge (`IAccountDeletionService.PurgeAsync`) runs the same
fan-out and nothing else at the User aggregate: identity collapse belongs to the
`Account` contributor, so the orchestrator only drops the caches that key off
identity afterwards.

Every exported section having an erasure declaration is checked at runtime by
`GdprService.ExportForUserAsync` (per contributor, not a central list): a
missing declaration logs an error and the slice is still exported.
`tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs` covers what
that check can't: it discovers contributors by reflection over the same section
assemblies the runtime composes itself from, and requires no category be
claimed twice across the whole roster and no retention be left unexplained.

Append-only entities per `design-rules.md` §12 (`consent_records`, `audit_log`,
`budget_audit_logs`, `camp_polygon_histories`, `application_state_history`,
`team_join_request_state_history`) are not deleted — foreign keys are nulled or
the row is re-pointed at the anonymized user rather than a separate tombstone
user. The lawful basis for each retained category lives in the owning
contributor's `ErasureDeclaration`, which is the single source of truth: the
accounting and expense ledgers under Código de Comercio Art. 30 and Ley 58/2003
Art. 66, the membership/role/shift/team records under Ley Orgánica 1/2002
Arts. 11 and 14, the consent ledger under GDPR Art. 7(1), and the audit log
under GDPR Art. 30.
