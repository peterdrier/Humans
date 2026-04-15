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
│  15 section services, each implementing          │
│  IUserDataContributor:                            │
│                                                   │
│    ProfileService            UserService          │
│    AccountMergeService       ApplicationDecisionService
│    ConsentService            TeamService          │
│    RoleAssignmentService     ShiftSignupService   │
│    FeedbackService           NotificationInboxService
│    TicketQueryService        CampaignService      │
│    CampService               AuditLogService      │
│    BudgetService                                  │
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
| `UserEmails` | `ProfileService` | Array of `{ Email, IsVerified, IsOAuth, IsNotificationTarget, Visibility }`. |
| `Profile` | `ProfileService` | Single object with burner name, legal name, birthday (month/day only), city/country, lat/lng, bio, pronouns, contribution interests, board notes, membership tier, approval/suspension state, consent check state, emergency contact, created/updated instants. |
| `ContactFields` | `ProfileService` | Array of `{ FieldType, Label, Value, Visibility }`. |
| `VolunteerHistory` | `ProfileService` | Array of `{ Date, EventName, Description, CreatedAt }`. |
| `Languages` | `ProfileService` | Array of `{ LanguageCode, Proficiency }`. |
| `CommunicationPreferences` | `ProfileService` | Array of `{ Category, OptedOut, InboxEnabled, UpdatedAt, UpdateSource }`. |
| `Applications` | `ApplicationDecisionService` | Array of tier application records with `StateHistory` inline. |
| `Consents` | `ConsentService` | Array of `{ DocumentName, DocumentVersion, ExplicitConsent, ConsentedAt, IpAddress, UserAgent }`. |
| `TeamMemberships` | `TeamService` | Array of `{ TeamName, Role, JoinedAt, LeftAt, TeamRoles[] }`. |
| `TeamJoinRequests` | `TeamService` | Array of `{ TeamName, Status, Message, RequestedAt, ResolvedAt }`. |
| `RoleAssignments` | `RoleAssignmentService` | Array of `{ RoleName, ValidFrom, ValidTo }`. |
| `ShiftSignups` | `ShiftSignupService` | Array of `{ EventName, Department, RotaName, DayOffset, IsAllDay, Status, Enrolled, StatusReason, CreatedAt, ReviewedAt }`. |
| `VolunteerEventProfiles` | `ShiftSignupService` | Array of per-event profile records (skills, quirks, languages, dietary, allergies, intolerances, medical). |
| `GeneralAvailability` | `ShiftSignupService` | Array of `{ EventName, AvailableDayOffsets, UpdatedAt }`. |
| `ShiftTagPreferences` | `ShiftSignupService` | Array of `{ TagName }`. |
| `FeedbackReports` | `FeedbackService` | Array of feedback reports with nested `Messages[]`. |
| `Notifications` | `NotificationInboxService` | Array of `{ Title, Body, ActionUrl, Priority, Source, CreatedAt, ReadAt, ResolvedAt }`. |
| `TicketOrders` | `TicketQueryService` | Array of `{ BuyerName, BuyerEmail, TotalAmount, Currency, PaymentStatus, DiscountCode, PurchasedAt }`. |
| `TicketAttendeeMatches` | `TicketQueryService` | Array of `{ AttendeeName, AttendeeEmail, TicketTypeName, Price, Status }`. |
| `CampaignGrants` | `CampaignService` | Array of `{ CampaignTitle, Code, AssignedAt, RedeemedAt, EmailStatus }`. |
| `CampLeadAssignments` | `CampService` | Array of `{ CampSlug, Role, JoinedAt, LeftAt }`. |
| `AccountMergeRequests` | `AccountMergeService` | Array of `{ Status, Role, CreatedAt, ResolvedAt }` (Role is "Target" or "Source"). |
| `AuditLog` | `AuditLogService` | Array of `{ Action, EntityType, OccurredAt, Role }` (Role is "Actor" or "Subject"). |
| `BudgetAuditLog` | `BudgetService` | Array of `{ EntityType, FieldName, Description, OccurredAt }`. |

All instants are serialized as invariant ISO-8601 strings (e.g.
`2026-04-15T10:30:00Z`) via `NodaTime` extensions.

## Extending the export

Adding a new section:

1. Add the section name constant to `GdprExportSections`.
2. Make the owning service implement `IUserDataContributor`. Return a
   `UserDataSlice(sectionName, data)` with shape documented in a new table row
   above. Return `null` data when the user has no records in this section so
   the orchestrator drops the entry from the export.
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
