<!-- freshness:triggers
  src/Humans.Application/Services/Gdpr/**
  src/Humans.Application/Interfaces/Gdpr/**
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Web/Controllers/GuestController.cs
  src/Humans.Application/Services/Profiles/ProfileService.cs
  src/Humans.Application/Services/Users/UserService.cs
  src/Humans.Application/Services/Consent/ConsentService.cs
  src/Humans.Application/Services/Teams/TeamService.cs
  src/Humans.Application/Services/Auth/RoleAssignmentService.cs
  src/Humans.Application/Services/Shifts/ShiftSignupService.cs
  src/Humans.Application/Services/Feedback/FeedbackService.cs
  src/Humans.Application/Services/Notifications/NotificationInboxService.cs
  src/Humans.Application/Services/Tickets/TicketQueryService.cs
  src/Humans.Application/Services/Campaigns/CampaignService.cs
  src/Humans.Application/Services/Camps/**
  src/Humans.Application/Services/AuditLog/**
  src/Humans.Application/Services/Budget/BudgetService.cs
  src/Humans.Application/Services/Users/AccountMergeService.cs
  src/Humans.Application/Services/Surveys/SurveyService.cs
  src/Humans.Application/Services/Governance/ApplicationDecisionService.cs
  src/Humans.Application/Services/Agent/AgentService.cs
  src/Humans.Application/Services/Events/EventService.cs
  src/Humans.Application/Services/Issues/IssuesService.cs
  src/Humans.Application/Services/Expenses/ExpenseReportService.cs
  src/Humans.Application/Services/Finance/HoldedFinanceService.cs
  src/Humans.Application/Services/Gate/GateService.cs
-->
<!-- freshness:flag-on-change
  Contributor list, JSON section names/shapes, or fan-out orchestration may have shifted; per-section table must stay in sync with each contributor's slice.
-->

# GDPR Data Export

GDPR Article 15 gives every human the right to obtain a copy of all personal
data an organization holds about them. Humans satisfies this right through a
self-service download at `/Profile/Me/DownloadData` (for humans who already
have a profile) and `/Guest/DownloadData` (for authenticated accounts that have
not yet completed onboarding). Both endpoints produce the same JSON document
shape.

## Architecture

The export is assembled by `IGdprExportService` (in `Humans.Application`), a
pure orchestrator that owns no database tables and has no `DbContext`
dependency. It injects `IEnumerable<IUserDataContributor>` and fans out one
call per contributor, merging the returned slices into a single document keyed
by section name.

Every section service that owns user-scoped tables implements
`IUserDataContributor`. When a new user-scoped section is added, its owning
service gains an `IUserDataContributor` implementation (and a DI registration)
and the export automatically includes it — the orchestrator never needs to
change.

```
┌─────────────────────────┐
│ ProfileController /     │
│ GuestController         │
└────────────┬────────────┘
             │
             ▼  ExportForUserAsync(userId)
┌─────────────────────────────────────────────────┐
│             IGdprExportService                  │
│          (Humans.Application layer)             │
│                                                 │
│   foreach contributor in IEnumerable<IUDC>      │
│       slices += contributor.ContributeForUser() │
│   return { ExportedAt, ...merged slices }       │
└──────┬──────────────────────────────────────────┘
       │
       ▼  ContributeForUserAsync(userId)
┌──────────────────────────────────────────────────┐
│  21 section services, each implementing          │
│  IUserDataContributor:                            │
│                                                   │
│    UserService               AccountMergeService  │
│    ApplicationDecisionService ConsentService      │
│    TeamService               RoleAssignmentService│
│    ShiftSignupService        FeedbackService      │
│    NotificationInboxService  TicketQueryService   │
│    CampaignService           CampService          │
│    AuditLogService           BudgetService        │
│    SurveyService             AgentService         │
│    EventService              IssuesService        │
│    ExpenseReportService      HoldedFinanceService │
│    GateService                                    │
└──────────────────────────────────────────────────┘
```

### Why sequential fan-out (not `Task.WhenAll`)

Every contributor in Humans uses the scoped `HumansDbContext` from the current
request. `DbContext` is not thread-safe — two concurrent awaits on the same
instance throw `InvalidOperationException`. A naive `Task.WhenAll` would
interleave contributor awaits on the shared context and corrupt state.

At ~500-user scale a sequential fan-out completes well under a second, so
parallelism would be a pure correctness hazard for no meaningful speedup. If a
future refactor gives each contributor its own context (via
`IDbContextFactory`), the loop in `GdprExportService.ExportForUserAsync` can
become parallel in place without changing the contract.

## Section registry

Section names are defined as constants in
`Humans.Application.Interfaces.Gdpr.GdprExportSections`. Renaming a value is a
breaking change for any human who has previously downloaded their export and
expects the same JSON keys on a re-download. Add new sections; don't rename
existing ones.

## JSON output shape

The top-level document is an object with `ExportedAt` (invariant ISO-8601 UTC
instant string) plus one key per section contributed. Sections whose owning
service has no data for this user are omitted.

| Section | Contributor | Shape |
|---------|-------------|-------|
| `Account` | `UserService` | Single object with user identity, display name, preferred language, Google email, deletion request/scheduled instants, created/last-login instants. |
| `EventParticipations` | `UserService` | Array of `{ Year, Status, Source, DeclaredAt }` covering every event-year the user has a participation row for (Ticketed / Attended / NoShow / NotAttending). |
| `UserEmails` | `UserService` | Array of `{ Email, IsVerified, IsOAuth, IsNotificationTarget, Visibility }`. |
| `Profile` | `UserService` | Single object with burner name, legal name, birthday (month/day only), city/country, lat/lng, bio, pronouns, contribution interests, board notes, membership tier, approval/suspension state, consent check state, emergency contact, created/updated instants. |
| `ContactFields` | `UserService` | Array of `{ FieldType, Label, Value, Visibility }`. |
| `VolunteerHistory` | `UserService` | Array of `{ Date, EventName, Description, CreatedAt }`. |
| `Languages` | `UserService` | Array of `{ LanguageCode, Proficiency }`. |
| `CommunicationPreferences` | `UserService` | Array of `{ Category, OptedOut, InboxEnabled, UpdatedAt, UpdateSource }`. |
| `Applications` | `ApplicationDecisionService` | Array of tier application records with `StateHistory` inline. |
| `Consents` | `ConsentService` | Array of `{ DocumentName, DocumentVersion, ExplicitConsent, ConsentedAt, IpAddress, UserAgent }`. |
| `TeamMemberships` | `TeamService` | Array of `{ TeamName, Role, JoinedAt, LeftAt, TeamRoles[] }`. |
| `TeamJoinRequests` | `TeamService` | Array of `{ TeamName, Status, Message, RequestedAt, ResolvedAt }`. |
| `TeamEarlyEntry` | `TeamService` | Array of `{ TeamName, ProjectName, EntryDate, GrantedAt }` — the user's team early-entry grants. |
| `RoleAssignments` | `RoleAssignmentService` | Array of `{ RoleName, ValidFrom, ValidTo }`. |
| `ShiftSignups` | `ShiftSignupService` | Array of `{ EventName, Department, RotaName, DayOffset, IsAllDay, Status, Enrolled, StatusReason, CreatedAt, ReviewedAt }`. |
| `VolunteerEventProfiles` | `ShiftSignupService` | Array of per-event profile records (skills, quirks, languages, dietary, allergies, intolerances, medical). |
| `GeneralAvailability` | `ShiftSignupService` | Array of `{ EventName, AvailableDayOffsets, UpdatedAt }`. |
| `ShiftTagPreferences` | `ShiftSignupService` | Array of `{ TagName }`. |
| `Events` | `EventService` | Single object with `{ Favourites: [{ GuideEventId, DayOffset, CreatedAt }], Preference: { ExcludedCategorySlugs, UpdatedAt } }` — the user's event favourites and category-exclusion preference; `Preference` is null when no preference row exists. |
| `FeedbackReports` | `FeedbackService` | Array of feedback reports with nested `Messages[]`. |
| `Issues` | `IssuesService` | Array of `{ Title, Description, Category, Section, Status, PageUrl, CreatedAt, ResolvedAt, Comments: [{ Content, IsFromUser, CreatedAt }] }` — issues filed by the user including their comments. |
| `Notifications` | `NotificationInboxService` | Array of `{ Title, Body, ActionUrl, Priority, Source, CreatedAt, ReadAt, ResolvedAt }`. |
| `TicketOrders` | `TicketQueryService` | Array of `{ BuyerName, BuyerEmail, TotalAmount, Currency, PaymentStatus, DiscountCode, PurchasedAt }`. |
| `TicketAttendeeMatches` | `TicketQueryService` | Array of `{ AttendeeName, AttendeeEmail, TicketTypeName, Price, Status }`. |
| `CampaignGrants` | `CampaignService` | Array of `{ CampaignTitle, Code, AssignedAt, RedeemedAt, EmailStatus }`. |
| `CampLeadAssignments` | `CampService` | Array of `{ CampSlug, Role, JoinedAt, LeftAt }`. |
| `CampRoleAssignments` | `CampService` | Array of `{ CampSlug, SeasonYear, RoleName, AssignedAt, AssignedByUserId }`. |
| `AccountMergeRequests` | `AccountMergeService` | Array of `{ Status, Role, CreatedAt, ResolvedAt }` (Role is "Target" or "Source"). |
| `AuditLog` | `AuditLogService` | Array of `{ Action, EntityType, OccurredAt, Role }` (Role is "Actor" or "Subject"). |
| `BudgetAuditLog` | `BudgetService` | Array of `{ EntityType, FieldName, Description, OccurredAt }`. |
| `AgentConversations` | `AgentService` | Array of `{ Id, StartedAt, LastMessageAt, Locale, MessageCount, Messages: [{ Role, Content, CreatedAt, Model, RefusalReason, HandedOffToFeedbackId }] }` — the user's AI assistant conversations with full message history. |
| `ExpenseReports` | `ExpenseReportService` | Array of `{ Id, Status, Note, PayeeName, PayeeIban (masked), Total, SubmittedAt, ApprovedAt, CreatedAt, Lines: [{ Id, Description, Amount, LineType, SortOrder, Attachment? }] }` — the user's expense reports including line items and attachment metadata; null when the user has no reports. |
| `ExpenseAuditLog` | `ExpenseReportService` | Single object `{ MaskedIban, Entries: [{ Action, EntityType, EntityId, Description, OccurredAt }] }` covering all expense-related audit events (submit, endorse, approve, reject, IBAN set/remove/reveal, etc.); null when the user has no expense audit entries. |
| `HoldedCreditorAccount` | `HoldedFinanceService` | Single object `{ SupplierAccountNum, HoldedContactId, Source }` — the user's Holded creditor account binding; null when no binding exists. |
| `SurveyResponses` | `SurveyService` | Array of `{ Survey, SubmittedAt, Culture, Answers[] }` where each answer has `{ Question, SelectedLabels, TextValue, RatingValue }`. |
| `GateScans` | `GateService` | Array of `{ OccurredAt, Verdict, Role, LaneId }` — the user's own gate activity, as guest or as scanner (`Role` is "Guest" or "Scanner"). Data-minimized: no barcode, no other person's identifiers. |

All instants are serialized as invariant ISO-8601 strings (e.g.
`2026-04-15T10:30:00Z`) via `NodaTime` extensions.

## Extending the export

Adding a new section:

1. Add the section name constant to `GdprExportSections`.
2. Make the owning service implement `IUserDataContributor`. Return a
   `UserDataSlice(sectionName, data)` with shape documented in a new table row
   above. **Null semantics:** for collection sections, always return the shaped
   collection (an empty list when the user has no records) — the legacy
   `ExportDataAsync` JSON shape always emitted collection top-level keys as
   `[]`, and downstream consumers depend on that stability. Return `null` data
   only for single-object sections whose underlying entity doesn't exist for
   this user (for example, a profileless account has no `Profile`). The
   orchestrator drops only `null` slices from the export.
3. Register the service in `InfrastructureServiceCollectionExtensions` using
   the forwarding pattern:

   ```csharp
   services.AddScoped<MyNewService>();
   services.AddScoped<IMyNewService>(sp => sp.GetRequiredService<MyNewService>());
   services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<MyNewService>());
   ```

4. Add the concrete type to
   `GdprExportDependencyInjectionTests.ExpectedContributorTypes` so the
   architecture test asserts the new contributor is accounted for.

The architecture test fails the build if a new class implements
`IUserDataContributor` in `Humans.Infrastructure` without being added to the
expected list, and fails if an expected contributor isn't wired in DI — so the
export can't silently drop a category.

## Right to deletion (Article 17)

Out of scope for the current refactor but designed for. A future
`IUserDataEraser` sibling interface will follow the same fan-out pattern for
GDPR Article 17 right-to-deletion. Append-only entities per DESIGN_RULES §7
(`consent_records`, `audit_log_entries`, `budget_audit_logs`,
`camp_polygon_histories`, `application_state_histories`,
`team_join_request_state_histories`) will not be deleted — foreign keys will be
nulled or rewritten to a tombstone user.
