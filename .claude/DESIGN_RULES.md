# Design Rules

Architectural rules governing how components interact. These are target-state rules — new code must follow them, existing code should be migrated incrementally (see [Migration Strategy](#migration-strategy)).

## 1. Layer Responsibilities

Clean Architecture with strict dependency direction: Web → Application → Domain. Infrastructure implements Application interfaces.

| Layer | Allowed | Not Allowed |
|-------|---------|-------------|
| **Domain** | Entities, enums, value objects. No dependencies on other layers. | Services, interfaces, framework references |
| **Application** | Interfaces, DTOs, use cases. Depends only on Domain. | DbContext, EF Core, HTTP, external SDKs |
| **Infrastructure** | Implements Application interfaces. EF Core, external API clients, background jobs. | Controller logic, Razor, HTTP request/response |
| **Web** | Controllers, views, view models, API endpoints. Depends on Application interfaces only. | DbContext, direct EF queries, direct cache access |

## 2. Service Ownership — The Core Rule

**Each service is the exclusive gateway to its data.** No other component — controller, service, job, or view component — may bypass the owning service to access its tables or cache.

### 2a. Controllers Cannot Talk to the Database

Controllers call services. Controllers never inject `DbContext`, never write EF queries, never access `IMemoryCache` for domain data. Their job is: receive HTTP request → call service(s) → return response.

**Exceptions:** `UserManager<User>` for ASP.NET Identity operations (login, password, claims) is allowed in controllers since Identity is a framework concern, not a domain service.

### 2b. Only Services Talk to the Database

All EF Core queries live in Infrastructure service implementations. If data needs to come from the database, it goes through a service method.

### 2c. Table Ownership Is Strict and Sectional

Each service owns specific database tables. **No other service may query, insert, update, or delete rows in tables it does not own.** If `CampService` needs a profile, it calls `IProfileService` — it does not query the `profiles` table.

### 2d. Cache Ownership Follows Data Ownership

The service that owns the data owns the cache for that data. Caching is an internal implementation detail of the owning service. Callers don't know whether data came from cache or DB — they call the service method and get the result.

- Only the owning service may read/write/invalidate cache entries for its data.
- Other services and controllers must not access `IMemoryCache` with another service's cache keys.

## 3. Table Ownership Map

Each section's service owns these tables. Cross-service access goes through the service interface, never direct DB queries.

| Section | Service(s) | Owned Tables |
|---------|-----------|--------------|
| **Profiles** | `ProfileService`, `ContactFieldService`, `UserEmailService`, `CommunicationPreferenceService`, `VolunteerHistoryService` | `profiles`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries` |
| **Users/Identity** | `UserService`, `AccountProvisioningService`, `DuplicateAccountService`, `AccountMergeService` | `AspNetUsers` (users), `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens` |
| **Teams** | `TeamService`, `TeamPageService`, `TeamResourceService` | `teams`, `team_members`, `team_join_requests`, `team_join_request_state_histories`, `team_role_definitions`, `team_role_assignments`, `team_pages` |
| **Auth** | `RoleAssignmentService` | `role_assignments` |
| **Governance** | `ApplicationDecisionService` | `applications`, `application_state_histories`, `board_votes` |
| **Legal & Consent** | `LegalDocumentService`, `AdminLegalDocumentService`, `ConsentService` | `legal_documents`, `document_versions`, `consent_records` |
| **Onboarding** | `OnboardingService` | *(no owned tables — orchestrates Profiles, Legal, Teams)* |
| **Camps** | `CampService`, `CampContactService` | `camps`, `camp_seasons`, `camp_leads`, `camp_images`, `camp_historical_names`, `camp_settings` |
| **City Planning** | `CityPlanningService` | `city_planning_settings`, `camp_polygons`, `camp_polygon_histories` |
| **Shifts** | `ShiftManagementService`, `ShiftSignupService`, `GeneralAvailabilityService` | `rotas`, `shifts`, `shift_signups`, `event_settings`, `general_availabilities`, `volunteer_event_profiles` |
| **Budget** | `BudgetService` | `budget_years`, `budget_groups`, `budget_categories`, `budget_line_items`, `budget_audit_logs`, `ticketing_projections` |
| **Tickets** | `TicketQueryService`, `TicketSyncService`, `TicketingBudgetService` | `ticket_orders`, `ticket_attendees`, `ticket_sync_states` |
| **Campaigns** | `CampaignService` | `campaigns`, `campaign_codes`, `campaign_grants` |
| **Team Resources** | `TeamResourceService` | `google_resources` |
| **Google Integration** | `GoogleSyncService`, `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `EmailProvisioningService` | `sync_service_settings` |
| **Email** | `EmailOutboxService`, `EmailService` | `email_outbox_messages` |
| **Feedback** | `FeedbackService` | `feedback_reports` |
| **Notifications** | `NotificationService`, `NotificationInboxService` | *(in-memory / transient)* |
| **Audit** | `AuditLogService` | `audit_log_entries` |
| **System Settings** | *(accessed via owning services)* | `system_settings` |
| **Unsubscribe** | `UnsubscribeService` | *(operates on User.UnsubscribedFromCampaigns via UserService)* |

See [`.claude/DEPENDENCY_GRAPH.md`](DEPENDENCY_GRAPH.md) for the full directed dependency graph with current vs target edges and circular dependency analysis.

## 4. Cross-Service Communication

When a service needs data from another section, it calls that section's service interface via constructor injection.

```csharp
// CORRECT — CampService needs a profile, asks ProfileService
public class CampService : ICampService
{
    private readonly IProfileService _profileService;
    
    public async Task<CampDetailDto> GetCampDetailAsync(Guid campId)
    {
        var camp = await _context.Camps.FindAsync(campId);
        var leadProfiles = await _profileService.GetProfilesByIdsAsync(camp.LeadUserIds);
        // ...
    }
}

// WRONG — CampService queries profiles table directly
public class CampService : ICampService
{
    public async Task<CampDetailDto> GetCampDetailAsync(Guid campId)
    {
        var camp = await _context.Camps.FindAsync(campId);
        var leads = await _context.Profiles.Where(p => leadIds.Contains(p.UserId)).ToListAsync();
        // ...
    }
}
```

### Cross-Service Contract Rules

- Cross-service calls are by **ID or small parameter set** — `GetProfileAsync(Guid userId)`, not raw queries.
- Services return **DTOs or domain entities** — never `IQueryable` or EF query fragments.
- Circular dependencies are resolved by extracting a shared interface or using an orchestrating service (like `OnboardingService` orchestrating Profiles + Legal + Teams).

## 5. Cross-Cutting Services

Some services are used across all sections. They own their own tables but are injected everywhere.

| Service | Purpose | Owned Tables |
|---------|---------|--------------|
| `RoleAssignmentService` | Temporal role memberships (Auth section) — the gateway for all role queries | `role_assignments` |
| `AuditLogService` | Append-only audit trail for user actions and sync operations | `audit_log_entries` |
| `EmailOutboxService` | Queue and track transactional emails | `email_outbox_messages` |
| `NotificationService` | In-app notifications | *(transient)* |

These are standalone services, not embedded in section services. Any service or controller can call `IAuditLogService.LogAsync(...)` to record an action, or `IRoleAssignmentService.HasActiveRoleAsync(...)` to check a role.

## 6. Authorization Pattern

Authorization uses **ASP.NET Core resource-based authorization** — one pattern, everywhere.

### How it works

Controllers call `IAuthorizationService.AuthorizeAsync(User, resource, requirement)`. Authorization handlers contain the logic. Services are auth-free — they trust the caller.

```csharp
// Controller — authorize, then call service
var authResult = await _authorizationService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit);
if (!authResult.Succeeded) return Forbid();
await _budgetService.DeleteLineItemAsync(id);
```

### Existing handlers

| Handler | Requirement | Resource | Purpose |
|---------|-------------|----------|---------|
| `TeamAuthorizationHandler` | `TeamOperationRequirement` | `Team` | Coordinator/manager/admin checks |
| `BudgetAuthorizationHandler` | `BudgetOperationRequirement` | `BudgetCategory` | Finance role + coordinator checks |
| `CampAuthorizationHandler` | `CampOperationRequirement` | `Camp` | Lead/CampAdmin checks |
| `RoleAssignmentAuthorizationHandler` | `RoleAssignmentOperationRequirement` | `string` (role name) | Who can assign which roles |
| `IsActiveMemberHandler` | `IsActiveMemberRequirement` | — | Membership gate |
| `HumanAdminOnlyHandler` | `HumanAdminOnlyRequirement` | — | Admin profile operations |

### Rules

- **No `isPrivileged` booleans.** Don't pass auth decisions as parameters to services. If the controller maps it wrong, the service silently does the wrong thing.
- **No inline `IsInRole` chains in controllers** for resource-scoped checks. Use the handler. `[Authorize(Roles = ...)]` is still fine for simple route-level role gates.
- **Services are auth-free.** They don't check roles, don't inject `IHttpContextAccessor`, don't receive boolean privilege flags. Authorization happens before the service is called.
- **New sections need a handler.** When adding a new section with resource-scoped auth, add a `*OperationRequirement` + `*AuthorizationHandler` pair. Don't invent a new pattern.

## 7. Immutable Entity Rules

Some entities are append-only. They have database triggers or application-level enforcement preventing UPDATE and DELETE.

| Entity | Table | Constraint |
|--------|-------|------------|
| `ConsentRecord` | `consent_records` | DB triggers block UPDATE and DELETE |
| `AuditLogEntry` | `audit_log_entries` | Append-only by convention |
| `BudgetAuditLog` | `budget_audit_logs` | Append-only by convention |
| `CampPolygonHistory` | `camp_polygon_histories` | Append-only by convention |
| `ApplicationStateHistory` | `application_state_histories` | Append-only by convention |
| `TeamJoinRequestStateHistory` | `team_join_request_state_histories` | Append-only by convention |

**Rule:** Never add UPDATE or DELETE logic for append-only entities. New state = new row.

## 8. Google Resource Ownership

All Google Drive resources are on **Shared Drives** (never My Drive). Google integration is managed by dedicated services:

- `GoogleSyncService` — syncs team membership to Drive/Groups
- `GoogleAdminService` — admin operations on Google Workspace
- `GoogleWorkspaceUserService` — user provisioning
- `SyncSettingsService` — per-service sync mode (None/AddOnly/AddAndRemove)

**No other service queries Google resources directly.** If a section needs to know about a team's Google resources, it asks `ITeamResourceService`.

## 9. DTO and ViewModel Boundary

- **Services** return DTOs or domain entities to callers.
- **Controllers** map service results to ViewModels for Razor views.
- **Domain entities** should not leak into Razor views when a ViewModel would provide better separation. For simple cases where the entity maps 1:1 to the view, passing the entity is acceptable.
- **View Components** may inject services directly (they are part of the Web layer) but still follow the service gateway rule — they call services, not DbContext.

## 10. Cache Invalidation Responsibility

The owning service decides when to invalidate its cache. Common patterns:

```csharp
// ProfileService owns profile caching
public class ProfileService : IProfileService
{
    public async Task<ProfileDto> GetProfileAsync(Guid userId)
    {
        return await _cache.GetOrCreateAsync($"profile:{userId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await LoadFromDb(userId);
        });
    }
    
    public async Task UpdateProfileAsync(Guid userId, UpdateProfileDto dto)
    {
        // ... save to DB ...
        _cache.Remove($"profile:{userId}");  // Owning service invalidates
    }
}

// WRONG — CampService invalidating ProfileService's cache
public class CampService : ICampService
{
    private readonly IMemoryCache _cache;
    
    public async Task UpdateCampLeadAsync(...)
    {
        // ...
        _cache.Remove($"profile:{leadUserId}");  // NOT YOUR CACHE
    }
}
```

If a cross-service operation requires cache invalidation, the owning service should expose a method for it (e.g., `IProfileService.InvalidateCacheAsync(Guid userId)`), or the invalidation should happen naturally when the owning service's write method is called.

## Migration Strategy

This is a target architecture. Existing code has violations. The migration approach:

1. **New code must comply.** All new features, services, and controllers follow these rules from day one.
2. **Touch-and-clean.** When modifying an existing controller or service for any reason, clean up violations in the code you're touching. Don't scope-creep into unrelated files.
3. **Tech debt cleanup.** Systematic violation cleanup is tracked as tech debt. Section-by-section migration can be done as dedicated work.
4. **Don't break working code for purity.** If a refactor to comply would require significant changes across multiple files, create a GitHub issue instead of a risky inline fix.

### Known Current Violations

Controllers with direct DbContext access (to be migrated):
- `AdminController` — queries DB directly for admin operations
- `ProfileController` — some direct DB queries
- `GoogleController` — direct DB access for sync operations
- `DevLoginController` — dev-only, low priority

Cross-service table access violations are documented per-section in `docs/sections/`.
