<!-- freshness:triggers
  src/Sections/Humans.Governance/**
  src/Sections/Humans.Gdpr/**
  src/Sections/Humans.Gdpr.Contracts/**
  src/Sections/Humans.Users/Controllers/ProfileController.cs
  src/Sections/Humans.Onboarding/Controllers/GuestController.cs
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

The export is assembled by `IGdprExportService` (declared on `Humans.Gdpr.Contracts`, implemented by the internal `GdprExportService` in `Humans.Gdpr`), a
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
│          (Humans.Gdpr section project)          │
│                                                 │
│   foreach contributor in IEnumerable<IUDC>      │
│       slices += contributor.ContributeForUser() │
│   return { ExportedAt, ...merged slices }       │
└──────┬──────────────────────────────────────────┘
       │
       ▼  ContributeForUserAsync(userId)
┌──────────────────────────────────────────────────┐
│  24 section services, each implementing           │
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
│    GateService               GoogleSyncLogService │
│    EmailOutboxService                             │
│    MailerLiteGdprContributor BackdoorApiKeyService │
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
`Humans.Gdpr.Contracts.GdprExportSections`. Renaming a value is a
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
| `CampRoleAssignments` | `CampService` | Array of `{ CampSlug, SeasonYear, RoleName, AssignedAt, AssignedByUserId }`. |
| `AccountMergeRequests` | `AccountMergeService` | Array of `{ Status, Role, CreatedAt, ResolvedAt }` (Role is "Target" or "Source"). |
| `AuditLog` | `AuditLogService` | Array of `{ Action, EntityType, OccurredAt, Role }` (Role is "Actor" or "Subject"). |
| `BudgetAuditLog` | `BudgetService` | Array of `{ EntityType, FieldName, Description, OccurredAt }`. |
| `AgentConversations` | `AgentService` | Array of `{ Id, StartedAt, LastMessageAt, Locale, MessageCount, Messages: [{ Role, Content, CreatedAt, Model, RefusalReason, HandedOffToFeedbackId }] }` — the user's AI assistant conversations with full message history. |
| `ExpenseReports` | `ExpenseReportService` | Array of `{ Id, Status, Note, PayeeName, PayeeIban (masked), Total, SubmittedAt, ApprovedAt, CreatedAt, Lines: [{ Id, Description, Amount, LineType, SortOrder, Attachment? }] }` — the user's expense reports including line items and attachment metadata; null when the user has no reports. |
| `ExpenseAuditLog` | `ExpenseReportService` | Single object `{ MaskedIban, Entries: [{ Action, EntityType, EntityId, Description, OccurredAt }] }` covering all expense-related audit events (submit, endorse, approve, reject, IBAN set/remove/reveal, etc.); null when the user has no expense audit entries. |
| `HoldedCreditorAccount` | `HoldedFinanceService` | Single object `{ SupplierAccountNum, HoldedContactId, Source }` — the user's Holded creditor account binding; null when no binding exists. |
| `SepaPayouts` | `HoldedFinanceService` | Array of `{ GeneratedAt, FileName, SupplierAccountNum, CreditorName, Iban (masked), Amount }` — every SEPA credit transfer paid to the user, oldest first; empty when they have never been paid. Retained after erasure on the fiscal basis. |
| `SurveyResponses` | `SurveyService` | Array of `{ Survey, SubmittedAt, Culture, Answers[] }` where each answer has `{ Question, SelectedLabels, TextValue, RatingValue }`. |
| `GateScans` | `GateService` | Array of `{ OccurredAt, Verdict, Role, LaneId }` — the user's own gate activity, as guest or as scanner (`Role` is "Guest" or "Scanner"). Data-minimized: no barcode, no other person's identifiers. |
| `GoogleSyncLog` | `GoogleSyncLogService` | Array of `{ Action, OccurredAt, Description, ResourceName, UserEmail, Role, Source, Success, ErrorMessage }` — every Workspace sync row attributed to the human, merge tombstones followed. |
| `EmailOutbox` | `EmailOutboxService` | Array of `{ RecipientEmail, RecipientName, Subject, HtmlBody, TemplateName, Status, CreatedAt, SentAt }` — the same per-user outbox history the human reads at `/Profile/Me/Outbox`. |
| `BackdoorApiKeys` | `BackdoorApiKeyService` | Array of `{ Label, DisplayPrefix, CreatedAt, LastUsedAt, RevokedAt }` — the machine-API keys allocated to the human; null when they hold none. The stored hash is never exported: it is the credential itself. |

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
3. Register the forwarding factory in the owning section's own
   `Section.Register` — it belongs beside the rest of that section's DI setup,
   not in a shared registration file:

   ```csharp
   services.AddScoped<MyNewService>();
   services.AddScoped<IMyNewService>(sp => sp.GetRequiredService<MyNewService>());
   services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<MyNewService>());
   ```

4. Add the concrete type to
   `GdprExportDependencyInjectionTests.ExpectedContributorTypes` so the
   architecture test asserts the new contributor is accounted for.

The architecture test scans `Humans.Web` (the host assembly, successor to the
deleted `Humans.Infrastructure`) plus every section assembly via
`SectionDiscoveryExtensions`, and fails the build if a new class implements
`IUserDataContributor` there without being added to the expected list, and
fails if an expected contributor isn't wired in DI — so the export can't
silently drop a category.

## Right to deletion (Article 17)

Erasure runs through the same fan-out, over the same interface
(nobodies-collective/Humans#853). `IUserDataContributor` carries two Article 17
members alongside `ContributeForUserAsync`, so a section cannot export a
category without accounting for its deletion:

- `ErasureDeclaration` — a **static** table, one entry per `GdprExportSections`
  key the contributor owns. `null` means erased or anonymized in full; a string
  names what survives and the lawful basis for keeping it. It must not touch
  instance state, the DbContext or the clock: the architecture test reads it
  from an uninitialized instance.
- `EraseForUserAsync(userId, ct)` — idempotent, because the job retries the
  whole cascade the next day after a mid-cascade failure.

`IAccountDeletionService`
(`src/Sections/Humans.Users/Services/AccountDeletionService.cs`) still owns the
30-day grace period — on request it revokes team memberships and governance
roles immediately — but once the grace period expires the daily
`ProcessAccountDeletionsJob` runs the fan-out rather than a hand-wired cascade.
Contributors run sequentially (scoped section DbContexts are not thread-safe,
same as the export), the contributor declaring `Account` runs last so sections
that still need the human's addresses can resolve them, and a contributor that
throws aborts the run with the deletion markers still set. Erasure also reaches
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

`tests/Humans.Web.Tests/Services/Gdpr/GdprErasureCoverageTests.cs` is the
enforcement: it discovers contributors by reflection over the same section
assemblies the runtime composes itself from, and requires the union of every
`ErasureDeclaration` to equal the full set of `GdprExportSections` constants,
with no category claimed twice and no retention left unexplained. Adding a
user-scoped section adds an export key, and the build stays red until some
contributor accounts for its erasure.

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
