# Service Entity Boundary Ratchet

## Rule

`I<Section>Service` read APIs must not expose EF/domain aggregate entities owned by that section. Service interfaces are section boundaries; callers outside the section should receive read models, DTOs, IDs, booleans, command result records, or other persistence-free shapes.

Repository interfaces are different. A section repository may return owned aggregate entities to its owning service because the repository is inside the persistence boundary. Repositories must still avoid cross-section navigation joins and must not shape data for presentation.

## Why

Returning entities from service interfaces leaks persistence shape across sections. Callers start depending on hydrated navs, EF-owned collection shape, and obsolete cross-section navigation properties. That makes caching harder, keeps one-off `GetSpecialCase*` methods alive, and turns interface growth into a permanent tax.

The target pattern is already visible in Profiles and Teams:

- Profiles: `FullProfile` is the canonical stitched read model.
- Teams: `TeamInfo` / `TeamMemberInfo` should become the canonical team read model.

## Policy

- `I*Service` interfaces must implement `IApplicationService`.
- `I*Repository` interfaces must implement `IRepository`.
- No new `I<Section>Service` read method may return an owned EF/domain entity.
- New read methods should first be challenged under `memory/architecture/interface-method-additions-are-debt.md`.
- Existing entity-returning service methods are allowed only as ratcheted debt.
- Each section cleanup PR should reduce the baseline below or replace entity-returning reads with section read models.
- Mutation methods may temporarily return entities when changing the command shape would create unnecessary churn, but new command result records are preferred.
- Controllers/views may sort/filter/page finite read models during view-model assembly.
- Cross-section callers must never use repositories to bypass the service boundary.
- Web classes must not inject repositories.
- Application services must not inject another section's repository; cross-section calls go through the owning service.

## Enforcement

The marker interfaces make the boundary searchable by humans, reforge, and tests:

- `IApplicationService`: application service boundary; read methods must not expose EF/domain entities.
- `IRepository`: persistence boundary; repositories may expose owned entities to their owning service.

`ServiceBoundaryArchitectureTests` pins the current state:

- all `I*Service` interfaces are marked as `IApplicationService`;
- all `I*Repository` interfaces are marked as `IRepository`;
- Web classes do not inject repositories;
- service read methods that already expose entities are baselined and must ratchet down;
- existing cross-section repository injections are baselined and must ratchet down.

## Audit Command

Baseline generated 2026-05-10 from branch `techdebt/2026-05-10-service-entity-boundary-ratchet`.

```powershell
$entities = Get-ChildItem src\Humans.Domain\Entities -Filter *.cs |
    ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) } |
    Sort-Object { $_.Length } -Descending
$pattern = '\b(' + (($entities | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'
Get-ChildItem src\Humans.Application\Interfaces -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\Repositories\\' } |
    ForEach-Object {
        $path=$_.FullName.Substring((Get-Location).Path.Length+1)
        Select-String -Path $_.FullName -Pattern $pattern |
            Where-Object {
                $_.Line -match 'Task<|ValueTask<|IReadOnly|List<|Dictionary<|record |\\)\\s*$' -and
                $_.Line -notmatch '^\s*///|<see cref|using Humans.Domain.Entities'
            } |
            ForEach-Object { '{0}:{1}: {2}' -f $path,$_.LineNumber,$_.Line.Trim() }
    }
```

The command is intentionally broad. Before fixing, classify each hit as:

- `Read leak`: service read returns owned entity/entities and should be replaced.
- `Command leak`: mutation returns an entity; replace when the command shape is otherwise being touched.
- `Value-object/DTO false positive`: name matched an entity token but is not an EF entity leak.
- `Infrastructure boundary`: connector/client/service wrapper where the entity is not an EF aggregate owned by the app.

## Baseline

### Teams

High priority. PR #474 introduces `TeamInfo` / `TeamMemberInfo`, so this section has a ready target shape.

Current leaks include:

- `ITeamService`: `Team`, `TeamMember`, `TeamJoinRequest`, `TeamRoleDefinition`, `TeamRoleAssignment` return types.
- `ITeamService.TeamDetailResult`: contains `Team`, child `Team` rows, and `TeamRoleDefinition` rows.
- `ITeamPageService.TeamPageDetailResult`: contains child `Team` rows and `TeamRoleDefinition` rows.
- `ITeamResourceService`: returns `GoogleResource`.
- `IMembershipQuery`: returns `TeamMember`.

Preferred direction:

- Expand `TeamInfo` only with stable read-model fields needed by callers.
- Remove narrow read helpers once callers can project from `GetTeamAsync` / `GetTeamsAsync`.
- Replace detail/admin/role/page entity returns with purpose-built result records.
- Keep role-assignment and membership mutations behind command methods returning IDs/result records.

### Profiles And Users

High priority after Teams. Profiles already has the strongest read-model precedent via `FullProfile`, but raw entity leaks remain.

Current leaks include:

- `IProfileService`: `Profile`, `ProfileLanguage`.
- `IUserEmailService`: `UserEmail`.
- `ICommunicationPreferenceService`: `CommunicationPreference`.
- `IUserService`: `User`, `EventParticipation`.
- `IAccountProvisioningService.AccountProvisioningResult`: contains `User`.
- Auth services: `IMagicLinkService` returns `User`.

Preferred direction:

- Use `FullProfile` or smaller profile DTOs for reads.
- Introduce user identity read models before removing `IUserService` raw `User` returns.
- Replace `UserEmail` entity reads with existing `UserEmailDto` / `UserEmailEditDto` or new snapshots.

### Shifts

High priority. Many read APIs expose owned shift entities, and Shifts is a frequent cross-section consumer/producer.

Current leaks include:

- `IShiftManagementService`: `EventSettings`, `Rota`, `Shift`, `ShiftTag`, `VolunteerEventProfile`.
- `IShiftSignupService`: `ShiftSignup`.
- `IGeneralAvailabilityService`: `GeneralAvailability`.

Preferred direction:

- Introduce event/rota/shift/signup read models before reducing interface surface.
- Keep repository/entity usage inside Shift services.
- Avoid reintroducing cross-section user/team navs while replacing return shapes.

### Camps

Medium-high priority. Cached finite data makes this a good fit for read-model consolidation after Teams.

Current leaks include:

- `ICampService`: `Camp`, `CampSettings`, `CampSeason`, `CampLead`, `CampImage`, `CampMember`.
- `ICampRoleService`: `CampRoleDefinition`, `CampRoleAssignment`.
- `ICityPlanningService`: `CampPolygon`, `CampPolygonHistory`, `CityPlanningSettings`.

Preferred direction:

- Use camp/season/member read models for browse/admin/detail flows.
- Replace command returns with result records where practical.

### Budget

Medium priority. Budget has many service methods returning owned entities.

Current leaks include:

- `IBudgetService`: `BudgetYear`, `BudgetGroup`, `BudgetCategory`, `BudgetLineItem`, `BudgetAuditLog`, `TicketingProjection`.
- `ITicketingBudgetService`: accepts `BudgetGroup`.

Preferred direction:

- Start with read pages: year/category/line-item detail DTOs.
- Move compute helpers off entity inputs if they are used outside the service implementation.

### Feedback, Issues, Campaigns, Calendar

Medium priority. These sections have smaller surfaces and should become good examples once larger patterns settle.

Current leaks include:

- `IFeedbackService`: `FeedbackReport`, `FeedbackMessage`.
- `IIssuesService`: `Issue`, `IssueComment`.
- `ICampaignService`: `Campaign`, `CampaignGrant`.
- `ICalendarService`: `CalendarEvent`.

Preferred direction:

- Replace list/detail reads with result records.
- Leave write methods until the read side is stable unless the command result shape is obvious.

### Legal And Consent

Medium priority. These sections expose document/consent entities and also compose team/profile data.

Current leaks include:

- `ILegalDocumentSyncService`: `LegalDocument`, `DocumentVersion`.
- `IAdminLegalDocumentService`: `LegalDocument`.
- `IConsentService`: `Team`, `DocumentVersion`, `ConsentRecord`.

Preferred direction:

- Replace consent group tuples with explicit read models.
- Replace legal document entities with document/version DTOs that do not expose persistence navs.

### Agent, Audit, Auth

Lower priority unless the interface is touched for feature work.

Current leaks include:

- `IAgentService`: `AgentConversation`.
- `IAuditLogService`: `AuditLogEntry`.
- `IRoleAssignmentService`: `RoleAssignment`.

Preferred direction:

- Audit views should use audit DTOs/read rows.
- Role-assignment admin screens should use role-assignment summaries.
- Agent history/admin reads should use conversation/message snapshots.

### Google, Email, Store, Miscellaneous

Mixed priority. Some hits are app-owned EF entities; some are connector/domain DTOs and need manual classification.

Current leaks include:

- `IGoogleSyncService`: `GoogleResource`, `GoogleSyncOutboxEvent`.
- `ISyncSettingsService`: `SyncServiceSettings`.
- `IEmailOutboxService`: `EmailOutboxMessage`.
- `IStoreService`: scan did not show major entity-returning reads, but store interfaces should be checked manually when touched.

Preferred direction:

- Do not block connector/client abstractions that naturally return external DTOs.
- Replace app-owned EF entities exposed through service interfaces when feature work touches the section.

## Suggested PR Order

1. Teams: use `TeamInfo` / `GetTeamsAsync` to remove the easiest narrow read helpers and raw `Team`/`TeamMember` read returns.
2. Profiles/Users: formalize `FullProfile` and user/email snapshots as service boundary shapes.
3. Shifts: introduce shift read models and remove raw signup/shift/rota reads.
4. Camps: mirror the Teams finite-cache pattern.
5. Budget, Feedback, Calendar, Legal, Consent: reduce section by section.
6. Audit/Auth/Agent/Google: clean opportunistically or when touched.

## Tracking

Update this table when a section PR lands.

| Section | Status | Canonical Read Model | Notes |
| --- | --- | --- | --- |
| Teams | In progress | `TeamInfo`, `TeamMemberInfo` | PR #474 establishes cache/read-model groundwork. |
| Profiles | Existing pattern, leaks remain | `FullProfile` | Remove raw `Profile`/`ProfileLanguage` returns. |
| Users | Not started | TBD user identity snapshot | Raw `User` returns are widespread. |
| Shifts | Not started | TBD | Large service surface; avoid one giant PR. |
| Camps | Not started | TBD | Good finite-cache candidate. |
| Budget | Not started | TBD | Many entity-returning read APIs. |
| Feedback/Issues/Campaigns/Calendar | Not started | TBD | Smaller section-by-section cleanup. |
| Legal/Consent | Not started | TBD | Consent tuple shape should become explicit DTO. |
| Agent/Audit/Auth | Not started | TBD | Lower priority unless touched. |
