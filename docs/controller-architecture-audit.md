# Controller Architecture Audit

Living document. Last updated: 2026-05-26 (freshness-sweep regeneration).

## Part 1: Action Name Audit

### Summary
- Controllers audited: 80 (excludes 4 base classes: `ApiControllerBase`, `HumansControllerBase`, `HumansTeamControllerBase`, `HumansCampControllerBase`)
- Purposes and suggestions preserved from prior audit where the (method, verb) pair still exists; new actions default to a name-derived purpose and `OK`.

`docs/architecture/conventions.md` §"Action Naming" codifies the heuristics: `Index` is for listings, no redundant controller-name prefixes, no bare plural-noun collisions, no generic verbs (`View`/`Show`/`Process`/`Handle`), and conventional form-handler verbs (`Create`/`Edit`/`Delete`/`Confirm`/`Cancel`).

Newly added since the previous audit (purposes are name-derived defaults — review for accuracy): `CantinaController` (`/Cantina/Roster*`), `EarlyEntryRosterController` (`/Shifts/Admin/EarlyEntry`), and a previously-undocumented `DebugController` (`/Debug/ClientStats`).

New actions on existing controllers: `ProfileController.DietaryMedical` (GET+POST), `FinanceController.HoldedAccounts` / `ProvisionHoldedAccounts` / `HoldedUnmatched` / `RunHoldedSync` (Holded creditor integration), `VolunteerTrackingController.ExportXlsx`, and `TicketTransferController.Confirm` replacing the prior `Lookup` action.

---

## AboutController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /About | GET | Public About page (license, packages, credits) | OK |
| Staff | /About/Staff | GET | Staff & roles directory grouped by role | OK |

## AccountController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Login | /Account/Login | GET | Login page | OK |
| ExternalLogin | /Account/ExternalLogin | POST | Initiate Google OAuth | OK |
| ExternalLoginCallback | /Account/ExternalLoginCallback | GET | Handle OAuth callback (link/create/lockout-recover) | OK |
| MagicLinkRequest | /Account/MagicLinkRequest | POST | Request magic link email | OK |
| MagicLinkConfirm | /Account/MagicLinkConfirm | GET | Magic link landing page (POST gate vs scanners) | OK |
| MagicLink | /Account/MagicLink | POST | Verify magic link token and sign in | OK |
| MagicLinkSignup | /Account/MagicLinkSignup | GET | New user signup landing via magic link | OK |
| CompleteSignup | /Account/CompleteSignup | POST | Finalize magic link signup | OK |
| Logout | /Account/Logout | POST | Sign out | OK |
| AccessDenied | /Account/AccessDenied | GET | Access denied page | OK |

## AdminAgentController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Agent/Admin | GET | Redirect to Status | OK |
| Status | /Agent/Admin/Status | GET | Agent service status dashboard | OK |
| Settings | /Agent/Admin/Settings | GET | Agent settings form | OK |
| Settings | /Agent/Admin/Settings | POST | Save agent settings | OK |
| ConversationPrompt | /Agent/Admin/Conversations/{id:guid}/Prompt | GET | View the resolved prompt for a conversation | OK |

## AdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Admin | GET | Admin dashboard | OK |
| PurgeHuman | /Admin/Humans/{id}/Purge | POST | Purge a human (non-prod) | OK |
| Logs | /Admin/Logs | GET | View in-memory logs | OK |
| Maintenance | /Admin/Maintenance | GET | Maintenance | OK |
| Configuration | /Admin/Configuration | GET | View configuration status | OK |
| DbVersion | /Admin/DbVersion | GET | Database migration info (anonymous) | OK |
| DbStats | /Admin/DbStats | GET | DB query statistics | OK |
| ResetDbStats | /Admin/DbStats/Reset | POST | Reset DB query statistics | OK |
| ClearHangfireLocks | /Admin/ClearHangfireLocks | POST | Clear stale Hangfire locks | OK |
| BackfillUserEmailProviders | /Admin/BackfillUserEmailProviders | GET | Backfill user email providers | OK |
| BackfillUserEmailProvidersRun | /Admin/BackfillUserEmailProviders | POST | Backfill user email providers run | OK |
| CacheStats | /Admin/CacheStats | GET | Cache hit/miss/size statistics | OK |
| ResetCacheStats | /Admin/CacheStats/Reset | POST | Reset cache statistics | OK |
| AudienceSegmentation | /Admin/Audience | GET | Audience segmentation (profile × ticket) | OK |

## AdminDuplicateAccountsController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Admin/DuplicateAccounts | GET | List duplicate-account groups | OK |
| Detail | /Admin/DuplicateAccounts/Detail | GET | Side-by-side duplicate detail | OK |
| Resolve | /Admin/DuplicateAccounts/Resolve | POST | Resolve a duplicate (archive source, re-link logins) | OK |

## AdminLegalDocumentsController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| LegalDocuments | /Legal/Admin/Documents | GET | List legal documents | OK |
| CreateLegalDocument | /Legal/Admin/Documents/Create | GET | Create legal document form | OK |
| CreateLegalDocument | /Legal/Admin/Documents/Create | POST | Submit new legal document | OK |
| EditLegalDocument | /Legal/Admin/Documents/{id}/Edit | GET | Edit legal document form | OK |
| EditLegalDocument | /Legal/Admin/Documents/{id}/Edit | POST | Submit legal document edits | OK |
| ArchiveLegalDocument | /Legal/Admin/Documents/{id}/Archive | POST | Archive a legal document | OK |
| SyncLegalDocument | /Legal/Admin/Documents/{id}/Sync | POST | Sync legal document from GitHub | OK |
| UpdateVersionSummary | /Legal/Admin/Documents/{id}/Versions/{versionId}/Summary | POST | Update version change summary | OK |

## AdminMergeController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Admin/MergeRequests | GET | List pending merge requests | OK |
| Detail | /Admin/MergeRequests/{id} | GET | Merge request detail | OK |
| Accept | /Admin/MergeRequests/{id}/Accept | POST | Accept a merge request | OK |
| Reject | /Admin/MergeRequests/{id}/Reject | POST | Reject a merge request | OK |

## AgentApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| List | /api/agent/conversations | GET | List conversations (API) | OK |
| Get | /api/agent/conversations/{id:guid} | GET | Get a conversation (API) | OK |
| GetMessages | /api/agent/conversations/{id:guid}/messages | GET | Get conversation messages (API) | OK |

## AgentController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Ask | /Agent/Ask | POST | Stream an agent answer (SSE) | OK |
| Conversations | /Agent/Conversations | GET | List own agent conversations | OK |
| Conversation | /Agent/Conversation/{id:guid} | GET | Conversation (single) | OK |
| ConversationDetail | /Agent/Conversations/{id:guid} | GET | Conversation detail | OK |

## AuditLogController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /AuditLog | GET | Audit log landing | OK |
| CheckDriveActivity | /AuditLog/CheckDriveActivity | POST | Check drive activity | OK |
| Resource | /AuditLog/Resource/{id:guid} | GET | Resource sync audit detail | OK |
| Human | /AuditLog/Human/{id:guid} | GET | Human sync audit detail | OK |

## BudgetController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Budget | GET | Coordinator budget view | OK |
| Summary | /Budget/Summary | GET | Public budget summary | OK |
| CategoryDetail | /Budget/Category/{id} | GET | Budget category detail | OK |
| CreateLineItem | /Budget/LineItems/Create | POST | Create a line item | OK |
| UpdateLineItem | /Budget/LineItems/{id}/Update | POST | Update a line item | OK |
| DeleteLineItem | /Budget/LineItems/{id}/Delete | POST | Delete a line item | OK |

## CalendarController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Calendar | GET | Month calendar view | OK |
| List | /Calendar/List | GET | List view of upcoming events | OK |
| Agenda | /Calendar/Agenda | GET | Agenda (timeline) view | OK |
| Team | /Calendar/Team/{teamId} | GET | Team-scoped calendar | OK |
| Event | /Calendar/Event/{id} | GET | Event detail | OK |
| Create | /Calendar/Event/Create | GET | Create event form | OK |
| Create | /Calendar/Event/Create | POST | Submit new event | OK |
| Edit | /Calendar/Event/{id}/Edit | GET | Edit event form | OK |
| Edit | /Calendar/Event/{id}/Edit | POST | Submit event edits | OK |
| Delete | /Calendar/Event/{id}/Delete | POST | Delete an event | OK |
| CancelOccurrence | /Calendar/Event/{id}/Occurrence/{originalStartUtc}/Cancel | POST | Cancel a single recurrence | OK |
| EditOccurrence | /Calendar/Event/{id}/Occurrence/{originalStartUtc}/Edit | GET | Edit occurrence override form | OK |
| EditOccurrence | /Calendar/Event/{id}/Occurrence/{originalStartUtc}/Edit | POST | Save occurrence override | OK |

## CampAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Camps/Admin (or /Barrios/Admin) | GET | Camp admin dashboard | OK |
| Approve | /Camps/Admin/Approve/{seasonId} | POST | Approve a camp season | OK |
| Reject | /Camps/Admin/Reject/{seasonId} | POST | Reject a camp season | OK |
| OpenSeason | /Camps/Admin/OpenSeason | POST | Open a season for registration | OK |
| CloseSeason | /Camps/Admin/CloseSeason/{year} | POST | Close a season | OK |
| SetPublicYear | /Camps/Admin/SetPublicYear | POST | Set the public display year | OK |
| SetNameLockDate | /Camps/Admin/SetNameLockDate | POST | Set name lock date for a season | OK |
| SetCampSeasonEeSlotCount | /Camps/Admin/SetCampSeasonEeSlotCount/{seasonId:guid} | POST | Set camp season early-entry slot count | OK |
| SetEeStartDate | /Camps/Admin/SetEeStartDate | POST | Set early-entry start date | OK |
| Reactivate | /Camps/Admin/Reactivate/{seasonId} | POST | Reactivate a withdrawn season | OK |
| ExportCamps | /Camps/Admin/Export | GET | Export camps as CSV | OK |
| UpdateRegistrationInfo | /Camps/Admin/UpdateRegistrationInfo | POST | Update camps registration info banner | OK |
| Delete | /Camps/Admin/Delete | POST | Delete a camp (Admin only) | OK |
| Roles | /Camps/Admin/Roles | GET | List camp role definitions | OK |
| RolesDrillDown | /Camps/Admin/Roles/{slug} | GET | Role assignments for one camp | OK |
| CreateRole | /Camps/Admin/Roles/Create | GET | Create role form | OK |
| CreateRole | /Camps/Admin/Roles/Create | POST | Submit new role | OK |
| EditRole | /Camps/Admin/Roles/{id:guid}/Edit | GET | Edit role form | OK |
| EditRole | /Camps/Admin/Roles/{id:guid}/Edit | POST | Submit role edits | OK |
| DeactivateRole | /Camps/Admin/Roles/{id:guid}/Deactivate | POST | Deactivate role | OK |
| ReactivateRole | /Camps/Admin/Roles/{id:guid}/Reactivate | POST | Reactivate role | OK |
| SeedSystemRoles | /Camps/Admin/SeedSystemRoles | POST | Seed system role definitions | OK |
| Compliance | /Camps/Admin/Compliance | GET | Camp compliance overview | OK |

## CampApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| GetCamps | /api/camps/{year} (or /api/barrios/{year}) | GET | Public camp summaries for a year | OK |
| GetPlacement | /api/camps/{year}/placement | GET | Camp placement summaries | OK |

## CampController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Camps | GET | Camp directory | OK |
| Details | /Camps/{slug} | GET | Camp detail page | OK |
| SeasonDetails | /Camps/{slug}/Season/{year} | GET | Season-specific detail | OK |
| Contact | /Camps/{slug}/Contact | GET | Facilitated contact form | OK |
| Contact | /Camps/{slug}/Contact | POST | Send facilitated message | OK |
| Register | /Camps/Register | GET | Register new camp form | OK |
| Register | /Camps/Register | POST | Submit camp registration | OK |
| Edit | /Camps/{slug}/Edit | GET | Edit camp form | OK |
| Members | /Camps/{slug}/Edit/Members | GET | Camp member management | OK |
| Edit | /Camps/{slug}/Edit | POST | Submit camp edits | OK |
| OptIn | /Camps/{slug}/OptIn/{year} | POST | Opt in to a new season | OK |
| Withdraw | /Camps/{slug}/Withdraw/{seasonId} | POST | Withdraw from a season | OK |
| Rejoin | /Camps/{slug}/Rejoin/{seasonId} | POST | Rejoin a withdrawn season | OK |
| AddHistoricalName | /Camps/{slug}/HistoricalNames/Add | POST | Add a historical name | OK |
| RemoveHistoricalName | /Camps/{slug}/HistoricalNames/Remove/{nameId} | POST | Remove a historical name | OK |
| UploadImage | /Camps/{slug}/Images/Upload | POST | Upload camp image | OK |
| DeleteImage | /Camps/{slug}/Images/Delete/{imageId} | POST | Delete camp image | OK |
| ReorderImages | /Camps/{slug}/Images/Reorder | POST | Reorder camp images | OK |
| RequestMembership | /Camps/{slug}/Members/Request | POST | Request to join a camp | OK |
| WithdrawMembershipRequest | /Camps/{slug}/Members/Withdraw/{campMemberId} | POST | Withdraw a pending membership request | OK |
| LeaveMembership | /Camps/{slug}/Members/Leave/{campMemberId} | POST | Leave a camp | OK |
| ApproveMembership | /Camps/{slug}/Members/Approve/{campMemberId} | POST | Approve a membership request | OK |
| RejectMembership | /Camps/{slug}/Members/Reject/{campMemberId} | POST | Reject a membership request | OK |
| RemoveMembership | /Camps/{slug}/Members/Remove/{campMemberId} | POST | Remove an existing camp member | OK |
| SetMemberEarlyEntry | /Camps/{slug}/Members/{campMemberId:guid}/EarlyEntry | POST | Set member early entry | OK |
| AddMember | /Camps/{slug}/Members/Add | POST | Add a member directly | OK |
| AssignRole | /Camps/{slug}/Roles/Assign | POST | Assign role to a camp member | OK |
| AssignRoleByUser | /Camps/{slug}/Roles/AssignByUser | POST | Assign role by user | OK |
| UnassignRole | /Camps/{slug}/Roles/{assignmentId:guid}/Unassign | POST | Unassign role | OK |

## CampaignController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Admin/Campaigns | GET | List campaigns | OK |
| Create | /Admin/Campaigns/Create | GET | Create campaign form | OK |
| Create | /Admin/Campaigns/Create | POST | Submit new campaign | OK |
| Edit | /Admin/Campaigns/Edit/{id} | GET | Edit campaign form | OK |
| Edit | /Admin/Campaigns/Edit/{id} | POST | Submit campaign edits | OK |
| Detail | /Admin/Campaigns/{id} | GET | Campaign detail page | OK |
| ImportCodes | /Admin/Campaigns/{id}/ImportCodes | POST | Import discount codes from CSV | OK |
| GenerateCodes | /Admin/Campaigns/{id}/GenerateCodes | POST | Generate discount codes via vendor | OK |
| Activate | /Admin/Campaigns/{id}/Activate | POST | Activate a campaign | OK |
| Complete | /Admin/Campaigns/{id}/Complete | POST | Mark campaign complete | OK |
| SendWave | /Admin/Campaigns/{id}/SendWave | GET | Send wave preview page | OK |
| SendWave | /Admin/Campaigns/{id}/SendWave | POST | Execute send wave | OK |
| Resend | /Admin/Campaigns/Grants/{grantId}/Resend | POST | Resend code to a grant | OK |
| RetryAllFailed | /Admin/Campaigns/{id}/RetryAllFailed | POST | Retry all failed sends | OK |

## CantinaController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Roster | /Cantina/Roster | GET | Weekly cantina meal roster | OK |
| Csv | /Cantina/Roster/Csv | GET | Export weekly cantina roster as CSV | → `RosterCsv` ? (`Csv` alone is ambiguous next to `Roster` / `Day` / `DayCsv`) |
| Day | /Cantina/Roster/Day | GET | Single-day cantina roster | OK |
| DayCsv | /Cantina/Roster/Day/Csv | GET | Export single-day cantina roster as CSV | OK |

## CityPlanningApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| GetState | /api/city-planning/state | GET | Current placement state + polygons (JSON) | OK |
| GetCampPolygonHistory | /api/city-planning/camp-polygons/{campSeasonId}/history | GET | Polygon edit history for a camp season | OK |
| SaveCampPolygon | /api/city-planning/camp-polygons/{campSeasonId} | PUT | Save a camp's polygon | OK |
| RestoreCampPolygon | /api/city-planning/camp-polygons/{campSeasonId}/restore/{historyId} | POST | Restore a polygon from history | OK |
| ExportGeoJson | /api/city-planning/export.geojson | GET | Export all camp polygons as GeoJSON | OK |
| GetContainers | /api/city-planning/containers/{year:int} | GET | Get containers | OK |
| ExportContainersGeoJson | /api/city-planning/containers/{year:int}/export.geojson | GET | Export containers as GeoJSON | OK |
| SaveContainerPlacement | /api/city-planning/containers/{id:guid}/placement/{year:int} | PUT | Save container placement | OK |
| UpdateContainerPlacementNotes | /api/city-planning/containers/{id:guid}/placement/{year:int}/notes | PUT | Update container placement notes | OK |
| ClearContainerPlacement | /api/city-planning/containers/{id:guid}/placement/{year:int} | DELETE | Clear container placement | OK |

## CityPlanningController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /CityPlanning | GET | City planning map (lead view) | OK |
| BarrioMap | /CityPlanning/BarrioMap | GET | Barrio map | OK |
| Admin | /CityPlanning/BarrioMap/Admin | GET | City planning admin dashboard | OK |
| OpenPlacement | /CityPlanning/BarrioMap/Admin/OpenPlacement | POST | Open placement window | OK |
| ClosePlacement | /CityPlanning/BarrioMap/Admin/ClosePlacement | POST | Close placement window | OK |
| OpenContainerPlacement | /CityPlanning/BarrioMap/Admin/OpenContainerPlacement | POST | Open container placement | OK |
| CloseContainerPlacement | /CityPlanning/BarrioMap/Admin/CloseContainerPlacement | POST | Close container placement | OK |
| UploadLimitZone | /CityPlanning/BarrioMap/Admin/UploadLimitZone | POST | Upload limit-zone GeoJSON | OK |
| UploadOfficialZones | /CityPlanning/BarrioMap/Admin/UploadOfficialZones | POST | Upload official zones GeoJSON | OK |
| DownloadLimitZone | /CityPlanning/BarrioMap/Admin/DownloadLimitZone | GET | Download stored limit-zone GeoJSON | OK |
| DownloadOfficialZones | /CityPlanning/BarrioMap/Admin/DownloadOfficialZones | GET | Download stored official zones GeoJSON | OK |
| DeleteLimitZone | /CityPlanning/BarrioMap/Admin/DeleteLimitZone | POST | Remove the limit-zone polygon | OK |
| DeleteOfficialZones | /CityPlanning/BarrioMap/Admin/DeleteOfficialZones | POST | Remove official zones | OK |
| UpdatePlacementDates | /CityPlanning/BarrioMap/Admin/UpdatePlacementDates | POST | Update placement window dates | OK |
| ContainerMap | /CityPlanning/ContainerMap/{year:int} | GET | Container map | OK |
| Containers | /CityPlanning/BarrioMap/Admin/Containers/{year:int} | GET | Container admin list | OK |
| CreateBarrioContainer | /CityPlanning/BarrioMap/Admin/Containers/Barrios/{campId}/Create | POST | Create barrio container | OK |
| EditContainer | /CityPlanning/BarrioMap/Admin/Containers/{id}/Edit | POST | Edit container | OK |
| DeleteContainer | /CityPlanning/BarrioMap/Admin/Containers/{id}/Delete | POST | Delete container | OK |

## ColorPaletteController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /ColorPalette | GET | Anonymous design reference page (no nav link) | OK |

## ConsentController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Consent | GET | Consent dashboard | OK |
| Review | /Consent/Review | GET | Review a document before consenting | OK |
| Submit | /Consent/Submit | POST | Submit consent | OK |

## ContainerController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Camp/{slug}/Containers | GET | List a camp's containers | OK |
| Create | /Camp/{slug}/Containers/Create | POST | Create a container | OK |
| Edit | /Camp/{slug}/Containers/{id}/Edit | POST | Edit a container | OK |
| Delete | /Camp/{slug}/Containers/{id}/Delete | POST | Delete a container | OK |

## DebugController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| ClientStats | /Debug/ClientStats | GET | Debug page returning client request/UA stats | OK |

## DevLoginController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| SignIn | /dev/login/{persona} | GET | Sign in as dev persona | OK |
| Users | /dev/login/users | GET | List real users for impersonation | OK |
| SignInAsUser | /dev/login/users/{id} | GET | Sign in as any user | OK |

## DevSeedController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| SeedBudget | /dev/seed/budget | POST | Seed sample budget data (non-prod) | OK |
| SeedCampRoles | /dev/seed/camp-roles | POST | Seed camp roles | OK |
| SeedDashboard | /dev/seed/dashboard | POST | Seed sample shift-dashboard data (non-prod) | OK |
| ResetDashboard | /dev/seed/dashboard/reset | POST | Reset dashboard | OK |

## EarlyEntryRosterController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Shifts/Admin/EarlyEntry | GET | Cross-source early-entry roster (camp roles + ticket stubs) | OK |

## EmailController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Email | GET | Email admin landing | OK |
| EmailOutbox | /Email/EmailOutbox | GET | View email outbox queue | → `Outbox` (the `Email` prefix is redundant on `EmailController`, and the route segment already says "EmailOutbox") |
| PauseEmailSending | /Email/EmailOutbox/Pause | POST | Pause email sending | OK |
| ResumeEmailSending | /Email/EmailOutbox/Resume | POST | Resume email sending | OK |
| RetryEmailOutboxMessage | /Email/EmailOutbox/Retry/{id} | POST | Retry a failed email | → `RetryOutboxMessage` ? (`Email` prefix duplicates the controller name) |
| DiscardEmailOutboxMessage | /Email/EmailOutbox/Discard/{id} | POST | Discard a queued email | → `DiscardOutboxMessage` ? (`Email` prefix duplicates the controller name) |
| EmailPreview | /Email/EmailPreview | GET | Preview all email templates | → `Preview` (`Email` prefix is redundant on `EmailController`) |

## EventsAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Settings | /Events/Admin/Settings | GET | Events settings form | OK |
| SaveSettings | /Events/Admin/Settings | POST | Save events settings | OK |
| Categories | /Events/Admin/Categories | GET | List event categories | OK |
| CreateCategory | /Events/Admin/Categories/Create | GET | Create category form | OK |
| CreateCategory | /Events/Admin/Categories/Create | POST | Submit new category | OK |
| EditCategory | /Events/Admin/Categories/{id:guid}/Edit | GET | Edit category form | OK |
| EditCategory | /Events/Admin/Categories/{id:guid}/Edit | POST | Submit category edits | OK |
| DeleteCategory | /Events/Admin/Categories/{id:guid}/Delete | POST | Delete category | OK |
| MoveCategoryUp | /Events/Admin/Categories/{id:guid}/MoveUp | POST | Move category up | OK |
| MoveCategoryDown | /Events/Admin/Categories/{id:guid}/MoveDown | POST | Move category down | OK |
| Venues | /Events/Admin/Venues | GET | List venues | OK |
| CreateVenue | /Events/Admin/Venues/Create | GET | Create venue form | OK |
| CreateVenue | /Events/Admin/Venues/Create | POST | Submit new venue | OK |
| EditVenue | /Events/Admin/Venues/{id:guid}/Edit | GET | Edit venue form | OK |
| EditVenue | /Events/Admin/Venues/{id:guid}/Edit | POST | Submit venue edits | OK |
| DeleteVenue | /Events/Admin/Venues/{id:guid}/Delete | POST | Delete venue | OK |
| MoveVenueUp | /Events/Admin/Venues/{id:guid}/MoveUp | POST | Move venue up | OK |
| MoveVenueDown | /Events/Admin/Venues/{id:guid}/MoveDown | POST | Move venue down | OK |

## EventsApiController

(`Controllers/Api/EventsApiController.cs`)

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| GetEvents | /api/events/events | GET | Get events | OK |
| GetEvent | /api/events/events/{id:guid} | GET | Get event | OK |
| GetBarrios | /api/events/barrios | GET | Get barrios | OK |
| GetBarrio | /api/events/barrios/{id:guid} | GET | Get barrio | OK |
| GetCategories | /api/events/categories | GET | Get categories | OK |
| GetPreferences | /api/events/preferences | GET | Get preferences | OK |
| UpdatePreferences | /api/events/preferences | PUT | Update preferences | OK |
| GetFavourites | /api/events/favourites | GET | Get favourites | OK |
| AddFavourite | /api/events/favourites/{eventId:guid} | POST | Add favourite | OK |
| RemoveFavourite | /api/events/favourites/{eventId:guid} | DELETE | Remove favourite | OK |

## EventsController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| MySubmissions | /Events/MySubmissions | GET | User's own event submissions | OK |
| Submit | /Events/Submit | GET | Individual event submission form | OK |
| Create | /Events/Submit | POST | Submit an individual event | OK |
| Edit | /Events/Submit/{eventId:guid}/Edit | GET | Edit own event submission form | OK |
| Update | /Events/Submit/{eventId:guid}/Edit | POST | Save event submission edits | OK |
| Withdraw | /Events/Submit/{eventId:guid}/Withdraw | POST | Withdraw an event submission | OK |
| Schedule | /Events/Schedule | GET | Personal event schedule | OK |
| Browse | /Events/Browse | GET | Browse the event programme | OK |
| ToggleFavourite | /Events/Browse/Favourite/{eventId:guid} | POST | Toggle favourite from Browse | OK |
| Unfavourite | /Events/Schedule/Unfavourite/{eventId:guid} | POST | Remove a favourite from Schedule | OK |
| BarrioSubmit | /Events/Barrio/{slug}/Submit | GET | Barrio event submission form | OK |
| BarrioCreate | /Events/Barrio/{slug}/Submit | POST | Submit a barrio event | OK |
| BarrioEdit | /Events/Barrio/{slug}/{eventId:guid}/Edit | GET | Edit barrio event form | OK |
| BarrioUpdate | /Events/Barrio/{slug}/{eventId:guid}/Edit | POST | Save barrio event edits | OK |
| BarrioWithdraw | /Events/Barrio/{slug}/{eventId:guid}/Withdraw | POST | Withdraw a barrio event | OK |
| BulkUploadTemplate | /Events/Barrio/{slug}/BulkUpload/Template | GET | Download barrio bulk-upload template | OK |
| BulkUploadImport | /Events/Barrio/{slug}/BulkUpload | POST | Bulk-import barrio events from a file | OK |

## EventsDashboardController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Events/Dashboard | GET | Events dashboard | OK |

## EventsExportController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Events/Export | GET | Events export landing | OK |
| DownloadCsv | /Events/Export/Csv | GET | Download events as CSV | OK |
| PrintGuide | /Events/Export/PrintGuide | GET | Printable programme guide | OK |

## EventsModerationController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Events/Moderate | GET | Event moderation queue | OK |
| Approve | /Events/Moderate/Approve | POST | Approve an event | OK |
| Reject | /Events/Moderate/Reject | POST | Reject an event | OK |
| Withdraw | /Events/Moderate/Withdraw | POST | Withdraw a submitted event (moderator) | OK |
| RequestEdit | /Events/Moderate/RequestEdit | POST | Request edits from submitter | OK |

## ExpensesController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Expenses | GET | My expense reports | OK |
| New | /Expenses/New | GET | New expense form | OK |
| New | /Expenses/New | POST | Create a new expense report | OK |
| Detail | /Expenses/{id:guid} | GET | Expense report detail | OK |
| Edit | /Expenses/{id:guid}/Edit | GET | Edit expense form | OK |
| Edit | /Expenses/{id:guid}/Edit | POST | Save expense edits | OK |
| AddLine | /Expenses/{id:guid}/Lines/Add | POST | Add an expense line | OK |
| UpdateLine | /Expenses/{id:guid}/Lines/Update | POST | Update an expense line | OK |
| RemoveLine | /Expenses/{id:guid}/Lines/{lineId:guid}/Remove | POST | Remove an expense line | OK |
| AttachFile | /Expenses/{id:guid}/Lines/{lineId:guid}/Attach | POST | Attach a file to a line | OK |
| RemoveAttachment | /Expenses/{id:guid}/Lines/{lineId:guid}/RemoveAttachment | POST | Remove a line attachment | OK |
| Submit | /Expenses/{id:guid}/Submit | POST | Submit an expense report | OK |
| Withdraw | /Expenses/{id:guid}/Withdraw | POST | Withdraw an expense report | OK |
| Iban | /Expenses/{id:guid}/Iban | GET | IBAN entry form | OK |
| Iban | /Expenses/{id:guid}/Iban | POST | Save IBAN for reimbursement | OK |
| Attachment | /Expenses/Attachment/{attachmentId:guid} | GET | Serve an expense attachment | OK |
| Coordinator | /Expenses/Coordinator | GET | Coordinator endorsement queue | OK |
| Endorse | /Expenses/{id:guid}/Endorse | POST | Coordinator endorse an expense | OK |
| CoordinatorReject | /Expenses/{id:guid}/CoordinatorReject | POST | Coordinator reject an expense | OK |
| Review | /Expenses/Review | GET | Finance review queue | OK |
| Approve | /Expenses/{id:guid}/Approve | POST | Approve an expense | OK |
| Reject | /Expenses/{id:guid}/Reject | POST | Reject an expense | OK |
| SepaGenerate | /Expenses/Sepa/Generate | POST | Generate SEPA payment file | OK |

## FeedbackApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| List | /api/feedback | GET | List feedback reports (API) | OK |
| Get | /api/feedback/{id} | GET | Get single feedback report (API) | OK |
| GetMessages | /api/feedback/{id}/messages | GET | Get messages for a report (API) | OK |
| PostMessage | /api/feedback/{id}/messages | POST | Post admin message (API) | OK |
| UpdateStatus | /api/feedback/{id}/status | PATCH | Update feedback status (API) | OK |
| UpdateAssignment | /api/feedback/{id}/assignment | PATCH | Update assignee (API) | OK |
| SetGitHubIssue | /api/feedback/{id}/github-issue | PATCH | Link GitHub issue (API) | OK |

## FeedbackController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Feedback | GET | Feedback list page | OK |
| Detail | /Feedback/{id} | GET | Feedback detail (AJAX partial or redirect) | OK |
| Submit | /Feedback | POST | Submit new feedback | OK |
| PostMessage | /Feedback/{id}/Message | POST | Post message on feedback thread | OK |
| UpdateStatus | /Feedback/{id}/Status | POST | Update feedback status | OK |
| UpdateAssignment | /Feedback/{id}/Assignment | POST | Update assignee on feedback | OK |
| SetGitHubIssue | /Feedback/{id}/GitHubIssue | POST | Link GitHub issue to feedback | OK |

## FinanceController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Finance | GET | Finance home (active year or no-year) | OK |
| YearDetail | /Finance/Years/{id} | GET | Budget year detail | OK |
| CategoryDetail | /Finance/Categories/{id} | GET | Budget category detail | OK |
| AuditLog | /Finance/AuditLog/{yearId?} | GET | Budget audit log | OK |
| CashFlow | /Finance/CashFlow | GET | Cash flow projection view | OK |
| Admin | /Finance/Admin | GET | Finance admin (manage years/groups) | OK |
| SyncDepartments | /Finance/Years/{id}/SyncDepartments | POST | Sync team departments into budget | OK |
| CreateYear | /Finance/Years/Create | POST | Create budget year | OK |
| UpdateYearStatus | /Finance/Years/{id}/UpdateStatus | POST | Update budget year status | OK |
| UpdateYear | /Finance/Years/{id}/Update | POST | Update budget year | OK |
| DeleteYear | /Finance/Years/{id}/Delete | POST | Delete budget year | OK |
| CreateGroup | /Finance/Groups/Create | POST | Create budget group | OK |
| UpdateGroup | /Finance/Groups/{id}/Update | POST | Update budget group | OK |
| DeleteGroup | /Finance/Groups/{id}/Delete | POST | Delete budget group | OK |
| CreateCategory | /Finance/Categories/Create | POST | Create budget category | OK |
| UpdateCategory | /Finance/Categories/{id}/Update | POST | Update budget category | OK |
| DeleteCategory | /Finance/Categories/{id}/Delete | POST | Delete budget category | OK |
| CreateLineItem | /Finance/LineItems/Create | POST | Create line item | OK |
| UpdateLineItem | /Finance/LineItems/{id}/Update | POST | Update line item | OK |
| DeleteLineItem | /Finance/LineItems/{id}/Delete | POST | Delete line item | OK |
| EnsureTicketingGroup | /Finance/Years/{id}/EnsureTicketingGroup | POST | Ensure the ticketing budget group exists | OK |
| UpdateTicketingProjection | /Finance/TicketingProjection/{groupId}/Update | POST | Update ticketing projection inputs | OK |
| SyncTicketingBudget | /Finance/TicketingBudget/{yearId}/Sync | POST | Sync ticketing budget from sales | OK |
| HoldedAccounts | /Finance/HoldedAccounts | GET | Holded creditor account provisioning view | OK |
| ProvisionHoldedAccounts | /Finance/HoldedAccounts/Provision | POST | Provision creditor accounts in Holded | OK |
| HoldedUnmatched | /Finance/HoldedUnmatched | GET | List unmatched Holded creditor entries | OK |
| RunHoldedSync | /Finance/HoldedSync/Run | POST | Trigger Holded sync (expense actuals) | OK |

## GoogleController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| SyncSettings | /Google/SyncSettings | GET | View sync service settings | OK |
| UpdateSyncSetting | /Google/SyncSettings | POST | Update a sync service mode | OK |
| SyncSystemTeams | /Google/SyncSystemTeams | POST | Trigger system team sync | OK |
| SyncResults | /Google/SyncResults | GET | View sync results | OK |
| CheckGroupSettings | /Google/CheckGroupSettings | POST | Check Google Group settings drift | OK |
| GroupSettingsResults | /Google/GroupSettingsResults | GET | View group settings check results | OK |
| RemediateGroupSettings | /Google/RemediateGroupSettings | POST | Remediate one group's settings | OK |
| RemediateAllGroupSettings | /Google/RemediateAllGroupSettings | POST | Remediate all drifted groups | OK |
| AllGroups | /Google/AllGroups | GET | List all domain groups | OK |
| LinkGroupToTeam | /Google/LinkGroupToTeam | POST | Link a Google Group to a team | OK |
| Sync | /Google/Sync | GET | Google sync status page | OK |
| SyncPreview | /Google/Sync/Preview/{resourceType} | GET | Preview sync (JSON) | OK |
| SyncExecute | /Google/Sync/Execute/{resourceId} | POST | Execute sync for a resource (JSON) | OK |
| SyncExecuteAll | /Google/Sync/ExecuteAll/{resourceType} | POST | Execute sync for all resources of type (JSON) | OK |
| ProvisionEmail | /Google/Human/{id}/ProvisionEmail | POST | Provision @nobodies.team email for a human | OK |
| Accounts | /Google/Accounts | GET | List @nobodies.team workspace accounts | OK |
| ProvisionAccount | /Google/Accounts/Provision | POST | Provision new workspace account | OK |
| SuspendAccount | /Google/Accounts/Suspend | POST | Suspend a workspace account | OK |
| ReactivateAccount | /Google/Accounts/Reactivate | POST | Reactivate a workspace account | OK |
| ResetPassword | /Google/Accounts/ResetPassword | POST | Reset workspace account password | OK |
| ResetPasswordAndGenerate2Fa | /Google/Accounts/ResetPasswordAndGenerate2Fa | POST | Reset password and generate 2FA | OK |
| LinkAccount | /Google/Accounts/Link | POST | Link workspace email to a human | OK |
| SyncOutbox | /Google/SyncOutbox | GET | View Google sync outbox events | OK |
| CheckEmailRenames | /Google/CheckEmailRenames | POST | Detect renamed Workspace emails | OK |
| EmailRenames | /Google/EmailRenames | GET | View detected email renames | OK |
| EmailFlagViolations | /Google/EmailFlagViolations | GET | Email flag violations | OK |
| Index | /Google | GET | Google integration admin landing | OK |

## GovernanceApplicationsController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Governance/Applications | GET | User's own applications list | OK |
| Create | /Governance/Applications/Create | GET | New tier application form | OK |
| Create | /Governance/Applications/Create | POST | Submit tier application | OK |
| Details | /Governance/Applications/Details/{id} | GET | View own application detail | OK |
| Withdraw | /Governance/Applications/Withdraw/{id} | POST | Withdraw own application | OK |
| Admin | /Governance/Applications/Admin | GET | Admin: filtered applications list | OK |
| AdminDetail | /Governance/Applications/Admin/{id} | GET | Admin: application detail with voting | OK |

## GovernanceBoardVotingController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| BoardVoting | /Governance/BoardVoting | GET | Board voting dashboard | OK |
| BoardVotingDetail | /Governance/BoardVoting/{applicationId} | GET | Board voting detail | OK |
| Vote | /Governance/BoardVoting/Vote | POST | Cast a board vote | OK |
| Finalize | /Governance/BoardVoting/Finalize | POST | Finalize application decision | OK |

## GovernanceController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Governance | GET | Governance info page (statutes, tier info) | OK |

## GuestController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Guest | GET | Profileless-account dashboard | OK |
| CommunicationPreferences | /Guest/CommunicationPreferences | GET | Comms prefs for profileless / unsubscribe-token | OK |
| UpdatePreference | /Guest/CommunicationPreferences/Update | POST | Update a comms preference | OK |
| DownloadData | /Guest/DownloadData | GET | GDPR data export for profileless account | OK |
| RequestDeletion | /Guest/RequestDeletion | POST | Request account deletion (profileless) | OK |
| CancelDeletion | /Guest/CancelDeletion | POST | Cancel pending deletion (profileless) | OK |

## GuideController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Guide | GET | Guide landing (README) | OK |
| Document | /Guide/{name} | GET | Render a specific guide page | OK |
| Refresh | /Guide/Refresh | POST | Refresh guide content from GitHub (Admin) | OK |

## HomeController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | / | GET | Landing page or authenticated dashboard | OK |
| DeclareNotAttending | /Home/DeclareNotAttending | POST | Mark self as not attending the active event | OK |
| UndoNotAttending | /Home/UndoNotAttending | POST | Undo a not-attending declaration | OK |
| Privacy | /Home/Privacy | GET | Privacy policy page | OK |
| Error | /Home/Error/{statusCode?} | GET | Error page | OK |

## IssuesApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| List | /api/issues | GET | List issues (API) | OK |
| Get | /api/issues/{id} | GET | Get single issue (API) | OK |
| Create | /api/issues | POST | Create an issue (API) | OK |
| GetComments | /api/issues/{id}/comments | GET | Get comments (API) | OK |
| PostComment | /api/issues/{id}/comments | POST | Post comment (API) | OK |
| UpdateStatus | /api/issues/{id}/status | PATCH | Update status (API) | OK |
| UpdateAssignee | /api/issues/{id}/assignee | PATCH | Update assignee (API) | OK |
| UpdateSection | /api/issues/{id}/section | PATCH | Update section (API) | OK |
| SetGitHubIssue | /api/issues/{id}/github-issue | PATCH | Link GitHub issue (API) | OK |

## IssuesController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Issues | GET | Issues list page | OK |
| New | /Issues/New | GET | New issue form | OK |
| Submit | /Issues | POST | Submit a new issue | OK |
| Detail | /Issues/{id} | GET | Issue detail | OK |
| PostComment | /Issues/{id}/Comments | POST | Post comment | OK |
| UpdateStatus | /Issues/{id}/Status | POST | Update status | OK |
| UpdateAssignee | /Issues/{id}/Assignee | POST | Update assignee | OK |
| UpdateSection | /Issues/{id}/Section | POST | Update section | OK |
| SetGitHubIssue | /Issues/{id}/GitHubIssue | POST | Link GitHub issue | OK |

## LanguageController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| SetLanguage | /Language/SetLanguage | POST | Change UI language preference | OK |

## LegalController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Legal/{slug?} | GET | Public legal document viewer | OK |

## LogApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Get | /api/logs | GET | Get recent log events (API) | OK |

## MailerAdminController

(`Controllers/Mailer/MailerAdminController.cs`)

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Mailer/Admin | GET | Mailer admin landing | OK |
| Debug | /Mailer/Admin/Audiences/{key}/Debug | GET | Inspect a single audience's members | OK |
| SyncAudience | /Mailer/Admin/Audiences/{key}/Sync | POST | Sync an audience | OK |
| Refresh | /Mailer/Admin/Refresh | POST | Refresh audience data | OK |
| Commit | /Mailer/Admin/Import/Commit | POST | Commit a staged import | OK |
| Import | /Mailer/Admin/Import | GET | Import preview page | OK |

## NotificationsController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Notifications | GET | Notifications list | OK |
| GetPopup | /Notifications/Popup | GET | Notification popup partial | OK |
| Resolve | /Notifications/Resolve/{id} | POST | Resolve a notification | OK |
| Dismiss | /Notifications/Dismiss/{id} | POST | Dismiss a notification | OK |
| MarkRead | /Notifications/MarkRead/{id} | POST | Mark a notification read | OK |
| MarkAllRead | /Notifications/MarkAllRead | POST | Mark all notifications read | OK |
| BulkResolve | /Notifications/BulkResolve | POST | Bulk resolve | OK |
| BulkDismiss | /Notifications/BulkDismiss | POST | Bulk dismiss | OK |
| ClickThrough | /Notifications/ClickThrough/{id} | GET | Record click-through and redirect | OK |

## OnboardingReviewController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /OnboardingReview | GET | Onboarding review queue | OK |
| Detail | /OnboardingReview/{userId} | GET | Review detail for a human | OK |
| Clear | /OnboardingReview/{userId}/Clear | POST | Clear consent check | OK |
| BulkClear | /OnboardingReview/BulkClear | POST | Bulk clear | OK |
| Flag | /OnboardingReview/{userId}/Flag | POST | Flag consent check | OK |
| Reject | /OnboardingReview/{userId}/Reject | POST | Reject a signup | OK |

## OnboardingWidgetController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /OnboardingWidget | GET | Onboarding widget shell | OK |
| Names | /OnboardingWidget/Names | GET | Names step form | OK |
| Names | /OnboardingWidget/Names | POST | Save names step | OK |
| Shifts | /OnboardingWidget/Shifts | GET | Shifts step | OK |
| SignUp | /OnboardingWidget/SignUp | POST | Sign up for a shift | OK |
| SignUpRange | /OnboardingWidget/SignUpRange | POST | Sign up for a shift range | OK |
| Skip | /OnboardingWidget/Skip | POST | Skip the shifts step | OK |
| Consents | /OnboardingWidget/Consents | GET | Consents step | OK |
| SignConsent | /OnboardingWidget/SignConsent | POST | Sign a consent | OK |

## ProfileAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| EmailProblems | /Profile/Admin/EmailProblems | GET | Email problems queue | OK |
| EmailProblemsCompare | /Profile/Admin/EmailProblems/Compare | GET | Compare two email-problem accounts | OK |
| Merge | /Profile/Admin/EmailProblems/Merge | POST | Merge two accounts | OK |
| DeleteOrphanEmail | /Profile/Admin/EmailProblems/DeleteOrphanEmail | POST | Delete an orphan email | OK |
| BackfillLegacyEmails | /Profile/Admin/EmailProblems/BackfillLegacyEmails | POST | Backfill legacy emails | OK |

## ProfileApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Search | /api/profiles/search | GET | Profile autocomplete API | OK |
| GetByUserId | /api/profiles/by-userid/{userId:guid} | GET | Get a profile by user id (API) | OK |

## ProfileBackfillAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Profile/Admin/Backfill | GET | Profile backfill landing | OK |
| Run | /Profile/Admin/Backfill/Run | POST | Run the profile backfill | OK |

## ProfileController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Profile | GET | Redirect to `Me` | OK |
| Me | /Profile/Me | GET | Own profile page | OK |
| Edit | /Profile/Me/Edit | GET | Edit own profile form | OK |
| Edit | /Profile/Me/Edit | POST | Submit profile edits | OK |
| Emails | /Profile/Me/Emails | GET | Email management page | OK |
| AddEmail | /Profile/Me/Emails/Add | POST | Add a new email | OK |
| VerifyEmail | /Profile/Me/Emails/Verify | GET | Verify email via token | OK |
| SetPrimary | /Profile/Me/Emails/SetPrimary | POST | Set primary email | OK |
| SetEmailVisibility | /Profile/Me/Emails/SetVisibility | POST | Change email visibility | OK |
| DeleteEmail | /Profile/Me/Emails/Delete | POST | Remove an email | OK |
| SetGoogle | /Profile/Me/Emails/SetGoogle | POST | Mark email as Google sign-in | OK |
| ClearGoogle | /Profile/Me/Emails/ClearGoogle | POST | Clear Google sign-in flag | OK |
| ClearPrimary | /Profile/Me/Emails/ClearPrimary | POST | Clear primary email | OK |
| Link | /Profile/Me/Emails/Link/{provider} | POST | Begin linking an external login provider | OK |
| Unlink | /Profile/Me/Emails/Unlink/{id:guid} | POST | Unlink an email | OK |
| UnlinkLinkedAccount | /Profile/Me/LinkedAccounts/Unlink | POST | Unlink an external login provider | OK |
| AdminEmails | /Profile/{id:guid}/Admin/Emails | GET | Admin: email management for a human | OK |
| AdminSetGoogle | /Profile/{id:guid}/Admin/Emails/SetGoogle | POST | Admin set Google flag | OK |
| AdminSetPrimary | /Profile/{id:guid}/Admin/Emails/SetPrimary | POST | Admin set primary | OK |
| AdminClearGoogle | /Profile/{id:guid}/Admin/Emails/ClearGoogle | POST | Admin clear Google flag | OK |
| AdminClearPrimary | /Profile/{id:guid}/Admin/Emails/ClearPrimary | POST | Admin clear primary | OK |
| AdminAddEmail | /Profile/{id:guid}/Admin/Emails/Add | POST | Admin add email | OK |
| AdminAddVerifiedEmail | /Profile/{id:guid}/Admin/Emails/AddVerified | POST | Admin add verified email | OK |
| AdminVerifyEmail | /Profile/{id:guid}/Admin/Emails/Verify | POST | Admin verify email | OK |
| AdminUnlink | /Profile/{id:guid}/Admin/Emails/Unlink/{emailId:guid} | POST | Admin unlink email | OK |
| AdminDeleteEmail | /Profile/{id:guid}/Admin/Emails/Delete | POST | Admin delete email | OK |
| AdminSetVisibility | /Profile/{id:guid}/Admin/Emails/SetVisibility | POST | Admin set visibility | OK |
| MyOutbox | /Profile/Me/Outbox | GET | View own email outbox | OK |
| Privacy | /Profile/Me/Privacy | GET | Privacy & data management page | → `DataPrivacy` ? (overlaps with `HomeController.Privacy`; this is the user's GDPR page, not the public privacy policy) |
| RequestDeletion | /Profile/Me/Privacy/RequestDeletion | POST | Request account deletion | OK |
| CancelDeletion | /Profile/Me/Privacy/CancelDeletion | POST | Cancel pending deletion | OK |
| ShiftInfo | /Profile/Me/ShiftInfo | GET | Shift profile info form | OK |
| ShiftInfo | /Profile/Me/ShiftInfo | POST | Submit shift profile info | OK |
| DietaryMedical | /Profile/Me/DietaryMedical | GET | Dietary & medical info form (moved from Shifts profile) | OK |
| DietaryMedical | /Profile/Me/DietaryMedical | POST | Submit dietary & medical info | OK |
| CommunicationPreferences | /Profile/Me/CommunicationPreferences | GET | Communication preferences page | OK |
| UpdatePreference | /Profile/Me/CommunicationPreferences/Update | POST | Update a single comms preference | OK |
| Notifications | /Profile/Me/Notifications | GET | Permanent redirect to CommunicationPreferences | OK |
| DownloadData | /Profile/Me/DownloadData | GET | GDPR data export | OK |
| Picture | /Profile/Picture | GET | Serve custom profile picture | OK |
| ImportGooglePhoto | /Profile/Me/ImportGooglePhoto | POST | Import Google photo | OK |
| ViewProfile | /Profile/{id} | GET | Public profile page for a human | OK |
| Popover | /Profile/{id}/Popover | GET | Mini profile popover (partial) | OK |
| SendMessage | /Profile/{id}/SendMessage | GET | Facilitated message form | OK |
| SendMessage | /Profile/{id}/SendMessage | POST | Send facilitated message | OK |
| Search | /Profile/Search | GET | Human search page | OK |
| AdminList | /Profile/Admin | GET | Admin: human list with filters | OK |
| Roles | /Profile/Admin/Roles | GET | Admin: governance role assignments list | OK |
| AdminDetail | /Profile/{id}/Admin | GET | Admin: human detail page | OK |
| RevealIban | /Profile/{id:guid}/Admin/RevealIban | POST | Reveal a human's IBAN | OK |
| AdminOutbox | /Profile/{id}/Admin/Outbox | GET | Admin: email outbox for a human | OK |
| SuspendHuman | /Profile/{id}/Admin/Suspend | POST | Suspend a human | OK |
| UnsuspendHuman | /Profile/{id}/Admin/Unsuspend | POST | Unsuspend a human | OK |
| ApproveVolunteer | /Profile/{id}/Admin/Approve | POST | Approve volunteer onboarding | OK |
| RejectSignup | /Profile/{id}/Admin/Reject | POST | Reject a signup | OK |
| AddRole | /Profile/{id}/Admin/Roles/Add | GET | Add role form | OK |
| AddRole | /Profile/{id}/Admin/Roles/Add | POST | Submit role assignment | OK |
| EndRole | /Profile/{id}/Admin/Roles/{roleId}/End | POST | End a role assignment | OK |

## ProfilePictureMigrationAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Profile/Admin/PictureMigration | GET | Picture migration landing | OK |
| Run | /Profile/Admin/PictureMigration/Run | POST | Run the picture migration | OK |

## ScannerController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Scanner | GET | Scanner section landing page | OK |
| Barcode | /Scanner/Barcode | GET | Browser-only barcode decode tool | OK |

## SearchController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Search | GET | Global search landing | OK |

## ShiftAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Teams/{slug}/Shifts | GET | Department shift admin dashboard | OK |
| CreateRota | /Teams/{slug}/Shifts/Rotas | POST | Create a rota | OK |
| EditRota | /Teams/{slug}/Shifts/Rotas/{rotaId} | POST | Update a rota | OK |
| ConfigureStaffing | /Teams/{slug}/Shifts/Rotas/{rotaId}/ConfigureStaffing | POST | Configure build/strike staffing grid | OK |
| GenerateShifts | /Teams/{slug}/Shifts/Rotas/{rotaId}/GenerateShifts | POST | Generate event shifts | OK |
| CreateShift | /Teams/{slug}/Shifts/Shifts | POST | Create a single shift | OK |
| EditShift | /Teams/{slug}/Shifts/Shifts/{shiftId} | POST | Update a shift | OK |
| ToggleVisibility | /Teams/{slug}/Shifts/Rotas/{rotaId}/ToggleVisibility | POST | Toggle rota volunteer visibility | OK |
| MoveRota | /Teams/{slug}/Shifts/Rotas/{rotaId}/Move | POST | Move a rota to another team | OK |
| EmailRota | /Teams/{slug}/Shifts/Rotas/{rotaId}/Email | GET | Email-rota compose form | OK |
| EmailRota | /Teams/{slug}/Shifts/Rotas/{rotaId}/Email | POST | Email the rota's volunteers | OK |
| DeleteRota | /Teams/{slug}/Shifts/Rotas/{rotaId}/Delete | POST | Delete a rota | OK |
| DeleteShift | /Teams/{slug}/Shifts/Shifts/{shiftId}/Delete | POST | Delete a shift | OK |
| BailRange | /Teams/{slug}/Shifts/BailRange | POST | Admin bail a signup range | OK |
| ApproveRange | /Teams/{slug}/Shifts/ApproveRange | POST | Approve a signup range | OK |
| RefuseRange | /Teams/{slug}/Shifts/RefuseRange | POST | Refuse a signup range | OK |
| ApproveSignup | /Teams/{slug}/Shifts/Signups/{signupId}/Approve | POST | Approve a signup | OK |
| RefuseSignup | /Teams/{slug}/Shifts/Signups/{signupId}/Refuse | POST | Refuse a signup | OK |
| MarkNoShow | /Teams/{slug}/Shifts/Signups/{signupId}/NoShow | POST | Mark signup as no-show | OK |
| RemoveSignup | /Teams/{slug}/Shifts/Signups/{signupId}/Remove | POST | Remove a signup | OK |
| SearchVolunteers | /Teams/{slug}/Shifts/SearchVolunteers | GET | Search volunteers for a shift (JSON) | OK |
| Voluntell | /Teams/{slug}/Shifts/Voluntell | POST | Assign a volunteer to a shift | OK |
| VoluntellRange | /Teams/{slug}/Shifts/VoluntellRange | POST | Assign a volunteer to a shift range | OK |
| SearchTags | /Teams/{slug}/Shifts/Tags/Search | GET | Tag autocomplete (JSON) | OK |
| CreateTag | /Teams/{slug}/Shifts/Tags/Create | POST | Create a new shift tag | OK |

## ShiftDashboardController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Shifts/Dashboard | GET | Cross-department shift dashboard | OK |
| SearchVolunteers | /Shifts/Dashboard/SearchVolunteers | GET | Search volunteers for a shift (JSON) | OK |
| Voluntell | /Shifts/Dashboard/Voluntell | POST | Assign volunteer from dashboard | OK |

## ShiftWorkloadAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Shifts/Admin/Workload | GET | Site-wide read-only shift workload dashboard | OK |

## ShiftsController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Shifts | GET | Browse all shifts | OK |
| SignUp | /Shifts/SignUp | POST | Sign up for a shift | OK |
| SignUpRange | /Shifts/SignUpRange | POST | Sign up for a shift range | OK |
| BailRange | /Shifts/BailRange | POST | Bail from a shift range | OK |
| Bail | /Shifts/Bail | POST | Bail from a single shift | OK |
| Mine | /Shifts/Mine | GET | My shifts page | OK |
| SaveAvailability | /Shifts/Mine/Availability | POST | Save general availability | OK |
| RegenerateIcal | /Shifts/Mine/RegenerateIcal | POST | Regenerate iCal URL | OK |
| SaveTagPreferences | /Shifts/Preferences/Tags | POST | Save preferred shift tags | OK |
| Settings | /Shifts/Settings | GET | Event settings form (Admin) | OK |
| Settings | /Shifts/Settings | POST | Save event settings (Admin) | OK |
| OrphanSignups | /Shifts/OrphanSignups | GET | Orphan signups | OK |

## StoreAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Catalog | /Store/Admin/Catalog | GET | Store product catalog | OK |
| Summary | /Store/Admin/Summary | GET | Store sales/order summary | OK |
| Edit | /Store/Admin/Catalog/Edit | GET | New product form | OK |
| Edit | /Store/Admin/Catalog/Edit/{id:guid} | GET | Edit product form | OK |
| Save | /Store/Admin/Catalog/Save | POST | Save a product | OK |
| Deactivate | /Store/Admin/Catalog/Deactivate/{id:guid} | POST | Deactivate a product | OK |

## StoreController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Store | GET | Store landing | OK |
| Order | /Store/Order/{id:guid} | GET | Order detail | OK |
| Pay | /Store/Order/{id:guid}/Pay | POST | Pay for an order | OK |
| Create | /Store/Order/Create/{campSeasonId:guid} | POST | Create an order | OK |
| AddLine | /Store/Order/{id:guid}/AddLine | POST | Add an order line | OK |
| RemoveLine | /Store/Order/{id:guid}/RemoveLine | POST | Remove an order line | OK |
| UpdateCounterparty | /Store/Order/{id:guid}/UpdateCounterparty | POST | Update order counterparty | OK |

## StoreStripeWebhookController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Receive | /Store/StripeWebhook | POST | Receive Stripe webhook events | OK |

## TeamAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| ApproveRequest | /Teams/{slug}/Requests/{requestId}/Approve | POST | Approve a join request | OK |
| RejectRequest | /Teams/{slug}/Requests/{requestId}/Reject | POST | Reject a join request | OK |
| Members | /Teams/{slug}/Members | GET | Team member list with admin actions | OK |
| RemoveMember | /Teams/{slug}/Members/{userId}/Remove | POST | Remove a member | OK |
| AddMember | /Teams/{slug}/Members/Add | POST | Add a member directly | OK |
| ProvisionEmail | /Teams/{slug}/Members/{userId}/ProvisionEmail | POST | Provision @nobodies.team email for a member | OK |
| SearchUsers | /Teams/{slug}/Members/Search | GET | Search users to add (JSON) | OK |
| Resources | /Teams/{slug}/Resources | GET | Team Google resources page | OK |
| LinkDriveResource | /Teams/{slug}/Resources/LinkDrive | POST | Link a Drive resource | OK |
| LinkGroup | /Teams/{slug}/Resources/LinkGroup | POST | Link a Google Group | OK |
| UpdatePermissionLevel | /Teams/{slug}/Resources/{resourceId}/PermissionLevel | POST | Set Drive resource permission level | OK |
| ToggleRestrictInheritedAccess | /Teams/{slug}/Resources/{resourceId}/RestrictInheritedAccess | POST | Toggle inherited-access restriction on a Shared Drive | OK |
| UnlinkResource | /Teams/{slug}/Resources/{resourceId}/Unlink | POST | Unlink a resource | OK |
| SyncResource | /Teams/{slug}/Resources/{resourceId}/Sync | POST | Sync a resource | OK |
| Roles | /Teams/{slug}/Roles | GET | Team role management page | OK |
| CreateRole | /Teams/{slug}/Roles/Create | POST | Create a team role | OK |
| EditRole | /Teams/{slug}/Roles/{roleId}/Edit | POST | Update a team role | OK |
| DeleteRole | /Teams/{slug}/Roles/{roleId}/Delete | POST | Delete a team role | OK |
| ToggleManagement | /Teams/{slug}/Roles/{roleId}/ToggleManagement | POST | Toggle management flag | OK |
| AssignRole | /Teams/{slug}/Roles/{roleId}/Assign | POST | Assign member to role | OK |
| UnassignRole | /Teams/{slug}/Roles/{roleId}/Unassign/{memberId} | POST | Unassign member from role | OK |
| EditPage | /Teams/{slug}/EditPage | GET | Edit team page content | OK |
| EditPage | /Teams/{slug}/EditPage | POST | Save team page content | OK |
| SearchMembersForRole | /Teams/{slug}/Roles/SearchMembers | GET | Search members for role assignment (JSON) | OK |

## TeamController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Teams | GET | Team directory | OK |
| Details | /Teams/{slug} | GET | Team detail/public page | OK |
| Birthdays | /Teams/Birthdays | GET | Birthday calendar | OK |
| Roster | /Teams/Roster | GET | Shift roster overview | OK |
| Map | /Teams/Map | GET | Member map | OK |
| MyTeams | /Teams/My | GET | Current user's teams | OK |
| Join | /Teams/{slug}/Join | GET | Join team form | OK |
| Join | /Teams/{slug}/Join | POST | Submit join request | OK |
| Leave | /Teams/{slug}/Leave | POST | Leave a team | OK |
| WithdrawRequest | /Teams/Requests/{id}/Withdraw | POST | Withdraw a join request | OK |
| Summary | /Teams/Summary | GET | Admin: team summary list | OK |
| CreateTeam | /Teams/Create | GET | Create team form | OK |
| CreateTeam | /Teams/Create | POST | Submit new team | OK |
| EditTeam | /Teams/{id}/Edit | GET | Edit team form | OK |
| EditTeam | /Teams/{id}/Edit | POST | Submit team edits | OK |
| DeleteTeam | /Teams/{id}/Delete | POST | Deactivate a team | OK |
| GetTeamGoogleResources | /Teams/{teamId}/GoogleResources | GET | Google resources dropdown for a team (JSON) | OK |

## TicketController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Tickets | GET | Ticket dashboard | OK |
| Orders | /Tickets/Orders | GET | Ticket orders list | OK |
| Attendees | /Tickets/Attendees | GET | Ticket attendees list | OK |
| Codes | /Tickets/Codes | GET | Discount code tracking | OK |
| GateList | /Tickets/GateList | GET | Gate list page | OK |
| WhoHasntBought | /Tickets/WhoHasntBought | GET | Who hasn't bought tickets | OK |
| SalesAggregates | /Tickets/SalesAggregates | GET | Weekly/quarterly sales data | OK |
| Sync | /Tickets/Sync | POST | Trigger ticket sync | OK |
| FullResync | /Tickets/FullResync | POST | Trigger full ticket re-sync | OK |
| ParticipationBackfill | /Tickets/Participation/Backfill | GET | Participation backfill form | OK |
| ParticipationBackfill | /Tickets/Participation/Backfill | POST | Apply participation backfill | OK |
| ExportAttendees | /Tickets/Export/Attendees | GET | Export attendees as CSV | OK |
| ExportOrders | /Tickets/Export/Orders | GET | Export orders as CSV | OK |

## TicketsContactsAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Tickets/Admin/Contacts | GET | Ticket contacts admin landing | OK |
| Apply | /Tickets/Admin/Contacts/Apply | POST | Apply contact matching | OK |

## TicketsOnsiteAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Tickets/Admin/Onsite | GET | "Who's onsite" roster (checked-in attendees) | OK |

## TicketTransferAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Tickets/Admin/Transfers | GET | Ticket transfer requests list | OK |
| Detail | /Tickets/Admin/Transfers/Detail/{id:guid} | GET | Transfer request detail | OK |
| Decide | /Tickets/Admin/Transfers/Decide | POST | Approve/reject a transfer | OK |
| RetryIssue | /Tickets/Admin/Transfers/{id:guid}/RetryIssue | POST | Retry a failed ticket issue | OK |

## TicketTransferController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Tickets/Transfers | GET | Transfer landing (own attendees + send/lookup form) | OK |
| Confirm | /Tickets/Transfers/Confirm | POST | Confirm a transfer (recipient + attendee preview) | OK |
| Submit | /Tickets/Transfers | POST | Submit a transfer | OK |
| Cancel | /Tickets/Transfers/Cancel | POST | Cancel a transfer | OK |

## TimezoneApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| SetTimezone | /api/timezone | POST | Set user timezone in session | OK |

## UnsubscribeController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Unsubscribe/{token} | GET | Validate token; redirect to comms prefs (or legacy confirm page) | → `Landing` ? (token-specific landing rather than a list — `Index` is misleading) |
| Confirm | /Unsubscribe/{token} | POST | Execute legacy unsubscribe | OK |
| OneClick | /Unsubscribe/OneClick | POST | RFC 8058 one-click unsubscribe | OK |

## UsersAdminDebugController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Users/Admin/Debug | GET | User identity debug page | OK |

## VolunteerTrackingController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Shifts/Dashboard/VolunteerTracking | GET | Volunteer tracking dashboard | OK |
| ExportXlsx | /Shifts/Dashboard/VolunteerTracking/ExportXlsx | GET | Export volunteer tracking grid as XLSX | OK |
| SetCampSetup | /Shifts/Dashboard/VolunteerTracking/SetCampSetup | POST | Set camp setup | OK |
| ClearCampSetup | /Shifts/Dashboard/VolunteerTracking/ClearCampSetup | POST | Clear camp setup | OK |
| SetDayOff | /Shifts/Dashboard/VolunteerTracking/SetDayOff | POST | Set day off | OK |
| ClearDayOff | /Shifts/Dashboard/VolunteerTracking/ClearDayOff | POST | Clear day off | OK |

## WelcomeController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Welcome | GET | Welcome / first-run page | OK |

## WidgetGalleryController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /WidgetGallery | GET | UI widget gallery (design reference) | OK |

---

## ViewComponents

ViewComponents don't have routes — they are invoked from views via `@await Component.InvokeAsync("Name")`. Listed for completeness:

| Component | Purpose |
|-----------|---------|
| ProfileCardViewComponent | Renders profile card with data fetching |
| NavBadgesViewComponent | Renders notification badges in nav |
| AccessMatrixViewComponent | Renders access permission matrix |
| FeedbackWidgetViewComponent | Renders floating feedback button |
| UserAvatarViewComponent | Renders user avatar with fallbacks |
| TempDataAlertsViewComponent | Renders TempData success/error alerts |
| ShiftSignupsViewComponent | Renders shift signup list |

---

## Rename Summary

| Controller | Current | Suggested | Reason |
|------------|---------|-----------|--------|
| EmailController | `EmailOutbox` / `RetryEmailOutboxMessage` / `DiscardEmailOutboxMessage` / `EmailPreview` | `Outbox` / `RetryOutboxMessage` / `DiscardOutboxMessage` / `Preview` | The `Email` prefix duplicates the controller name |
| ProfileController | `Privacy` | `DataPrivacy` ? | Avoids overlap with `HomeController.Privacy` (site policy vs user GDPR page) |
| UnsubscribeController | `Index` | `Landing` ? | This isn't a list page — it's a token-specific landing/redirect |

**Note:** Items marked with `?` are suggestions where the rename benefit is marginal — worth discussing but not critical.

**High-confidence renames (no `?`):**
1. `EmailController.EmailOutbox` → `Outbox` (and matching peers) — redundant prefix

All other actions have names that adequately describe what the user sees or what the action does, given their route context. The earlier `GoogleController` redundant-prefix renames (`GoogleSyncResourceAudit` / `HumanGoogleSyncAudit`) were resolved in PR #499 — those actions now live on `AuditLogController` as `Resource` / `Human`.

---

## Part 2: Misplaced Actions & Ideal Controller Breakdown

### Status of the original (#261) splits

Several of the splits proposed in the original audit have since shipped:

- **HumanController has been merged into `ProfileController`** — `View`/`Popover`/`SendMessage`, the admin actions (`AdminList`, `AdminDetail`, `AdminOutbox`, `SuspendHuman`, `UnsuspendHuman`, `ApproveVolunteer`, `RejectSignup`, `AddRole`, `EndRole`), and even the `Search` page now live on `/Profile`. The `View` → `HumanProfile` rename was implemented as `ViewProfile`.
- **GoogleController** absorbed all sync, workspace-account, and email-rename actions previously spread across `AdminController`, `BoardController`, `AdminEmailController`, and the team-controller `Sync*` actions. The two redundant-prefix Google audit actions later moved to `AuditLogController` (#499).
- **EmailController** is now its own controller (`/Email`) holding the email outbox + preview that previously lived on `AdminController`.
- **AdminDuplicateAccountsController** handles the duplicate-account workflow.
- **CalendarController** and **CityPlanningController** / **CityPlanningApiController** are dedicated sections.
- **GuestController** owns the profileless-account dashboard, with its own GDPR / comms-prefs actions parallel to `ProfileController`.
- **NotificationsController** is its own section.
- **GovernanceController.Roles moved to `ProfileController.Roles`** (`/Profile/Admin/Roles`) — the governance role-assignments admin list now lives with the human-admin surface, leaving `GovernanceController` as a single info page. (Note: this lands the admin role list on the user-profile controller; see the ProfileController split discussion below.)

### Remaining misplaced actions

#### TeamController — community features still on a team controller

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Birthdays` | TeamController | Community birthday calendar — not team-specific | **CommunityController** (`/Community/Birthdays`) |
| `Map` | TeamController | Member location map — not team-specific | **CommunityController** (`/Community/Map`) |
| `Roster` | TeamController | Shift roster — belongs with shift domain | **ShiftsController** |
| `Summary`, `CreateTeam`, `EditTeam`, `DeleteTeam` | TeamController | Admin team CRUD | **TeamAdminController** already exists — move these there (route: `/Teams/Admin/...`) |

#### ProfileController — multiple concerns in one (~55 actions)

The profile controller has grown — it now owns own-profile, email, linked-account, privacy, shift info, comms prefs, public viewing, the entire human-admin surface, and (newly) the governance role-assignments admin list.

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Index`, `Me`, `Edit` (GET+POST), `Picture`, `ImportGooglePhoto`, `ShiftInfo`, `ViewProfile`, `Popover`, `SendMessage`, `Search` | ProfileController | Core profile + public viewing — fine here | Stay |
| `Emails`, `AddEmail`, `VerifyEmail`, `SetPrimary`, `SetEmailVisibility`, `DeleteEmail`, `SetGoogle`, `ClearGoogle`, `ClearPrimary`, `Link`, `Unlink`, `UnlinkLinkedAccount` + all `Admin*Email*` actions | ProfileController | Email / linked-account management — large own sub-domain | **ProfileEmailController** (`/Profile/Me/Emails/...`) |
| `Privacy`, `RequestDeletion`, `CancelDeletion`, `DownloadData`, `MyOutbox` | ProfileController | GDPR/data rights | **ProfilePrivacyController** (`/Profile/Me/Privacy/...`) |
| `CommunicationPreferences`, `UpdatePreference`, `Notifications` | ProfileController | Communication prefs | Could stay or move to email controller |
| `AdminList`, `Roles`, `AdminDetail`, `AdminOutbox`, `RevealIban`, `SuspendHuman`, `UnsuspendHuman`, `ApproveVolunteer`, `RejectSignup`, `AddRole` (GET+POST), `EndRole` | ProfileController | Admin human management + role assignments — behind admin-policy overrides | **HumanAdminController** (`/Profile/Admin/...` or `/Humans/Admin/...`) |

#### ShiftsController — admin settings mixed with user browsing

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Index`, `SignUp`, `SignUpRange`, `Bail`, `BailRange`, `Mine`, `SaveAvailability`, `RegenerateIcal`, `SaveTagPreferences` | ShiftsController | User-facing shift browsing | Stay |
| `Settings` (GET+POST), `OrphanSignups` | ShiftsController | Admin-only event settings / diagnostics | **ShiftDashboardController** or a dedicated **EventSettingsController** |

### Priority Ranking for Splits

If tackling this incrementally, ordered by impact:

1. **ProfileController → HumanAdminController** — highest impact, ~11 admin actions (now including the governance `Roles` list) on a user-profile controller; clear `[Authorize]`-policy boundary makes the split mechanical.
2. **ProfileController → ProfileEmailController + ProfilePrivacyController** — email/linked-account + GDPR sub-domains across two clear boundaries.
3. **TeamController → TeamAdminController + CommunityController** — community features (`Birthdays`, `Map`, `Roster`) hiding on a team controller; admin team CRUD belongs on the existing `TeamAdminController`.
4. **ShiftsController → EventSettingsController** — minor, just the settings + orphan-signups actions.
