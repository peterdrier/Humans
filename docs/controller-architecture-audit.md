# Controller Architecture Audit

Living document. Last updated: 2026-04-25 (freshness sweep).

## Part 1: Action Name Audit

### Summary
- Controllers audited: 46 (excludes 3 abstract base classes: `HumansControllerBase`, `HumansTeamControllerBase`, `HumansCampControllerBase`)
- Renames suggested: 6
- Already OK: rest

`docs/architecture/conventions.md` does not codify a formal action-name convention, so the suggestions below flag the same kinds of ambiguities the original audit (#261) called out: overly generic names (`View`), redundant prefixes that duplicate the controller name, bare plural-noun method names that conflict with the controller's own name, and `Index` actions that are not really listings.

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

## AdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Admin | GET | Admin dashboard | OK |
| PurgeHuman | /Admin/Humans/{id}/Purge | POST | Purge a human (non-prod) | OK |
| Logs | /Admin/Logs | GET | View in-memory logs | OK |
| Configuration | /Admin/Configuration | GET | View configuration status | OK |
| DbVersion | /Admin/DbVersion | GET | Database migration info (anonymous) | OK |
| DbStats | /Admin/DbStats | GET | DB query statistics | OK |
| ResetDbStats | /Admin/DbStats/Reset | POST | Reset DB query statistics | OK |
| ClearHangfireLocks | /Admin/ClearHangfireLocks | POST | Clear stale Hangfire locks | OK |
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
| LegalDocuments | /Admin/LegalDocuments | GET | List legal documents | OK |
| CreateLegalDocument | /Admin/LegalDocuments/Create | GET | Create legal document form | OK |
| CreateLegalDocument | /Admin/LegalDocuments/Create | POST | Submit new legal document | OK |
| EditLegalDocument | /Admin/LegalDocuments/{id}/Edit | GET | Edit legal document form | OK |
| EditLegalDocument | /Admin/LegalDocuments/{id}/Edit | POST | Submit legal document edits | OK |
| ArchiveLegalDocument | /Admin/LegalDocuments/{id}/Archive | POST | Archive a legal document | OK |
| SyncLegalDocument | /Admin/LegalDocuments/{id}/Sync | POST | Sync legal document from GitHub | OK |
| UpdateVersionSummary | /Admin/LegalDocuments/{id}/Versions/{versionId}/Summary | POST | Update version change summary | OK |

## AdminMergeController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Admin/MergeRequests | GET | List pending merge requests | OK |
| Detail | /Admin/MergeRequests/{id} | GET | Merge request detail | OK |
| Accept | /Admin/MergeRequests/{id}/Accept | POST | Accept a merge request | OK |
| Reject | /Admin/MergeRequests/{id}/Reject | POST | Reject a merge request | OK |

## ApplicationController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Application | GET | User's own applications list | OK |
| Create | /Application/Create | GET | New tier application form | OK |
| Create | /Application/Create | POST | Submit tier application | OK |
| Details | /Application/Details | GET | View own application detail | OK |
| Withdraw | /Application/Withdraw | POST | Withdraw own application | OK |
| Applications | /Application/Admin | GET | Admin: filtered applications list | → `AdminList` ? ("Applications" on `ApplicationController` reads as a duplicate of `Index`; this is the admin list view) |
| ApplicationDetail | /Application/Admin/{id} | GET | Admin: application detail with voting | OK |

## BoardController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Board | GET | Board dashboard with onboarding stats | OK |
| AuditLog | /Board/AuditLog | GET | Board audit log | OK |

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
| Reactivate | /Camps/Admin/Reactivate/{seasonId} | POST | Reactivate a withdrawn season | OK |
| ExportCamps | /Camps/Admin/Export | GET | Export camps as CSV | OK |
| UpdateRegistrationInfo | /Camps/Admin/UpdateRegistrationInfo | POST | Update camps registration info banner | OK |
| Delete | /Camps/Admin/Delete | POST | Delete a camp (Admin only) | OK |

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
| Edit | /Camps/{slug}/Edit | POST | Submit camp edits | OK |
| OptIn | /Camps/{slug}/OptIn/{year} | POST | Opt in to a new season | OK |
| Withdraw | /Camps/{slug}/Withdraw/{seasonId} | POST | Withdraw from a season | OK |
| Rejoin | /Camps/{slug}/Rejoin/{seasonId} | POST | Rejoin a withdrawn season | OK |
| AddLead | /Camps/{slug}/Leads/Add | POST | Add a co-lead | OK |
| RemoveLead | /Camps/{slug}/Leads/Remove/{leadId} | POST | Remove a lead | OK |
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

## CityPlanningApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| GetState | /api/city-planning/state | GET | Current placement state + polygons (JSON) | OK |
| GetCampPolygonHistory | /api/city-planning/camp-polygons/{campSeasonId}/history | GET | Polygon edit history for a camp season | OK |
| SaveCampPolygon | /api/city-planning/camp-polygons/{campSeasonId} | PUT | Save a camp's polygon | OK |
| RestoreCampPolygon | /api/city-planning/camp-polygons/{campSeasonId}/restore/{historyId} | POST | Restore a polygon from history | OK |
| ExportGeoJson | /api/city-planning/export.geojson | GET | Export all camp polygons as GeoJSON | OK |

## CityPlanningController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /CityPlanning | GET | City planning map (lead view) | OK |
| Admin | /CityPlanning/Admin | GET | City planning admin dashboard | OK |
| OpenPlacement | /CityPlanning/Admin/OpenPlacement | POST | Open placement window | OK |
| ClosePlacement | /CityPlanning/Admin/ClosePlacement | POST | Close placement window | OK |
| UploadLimitZone | /CityPlanning/Admin/UploadLimitZone | POST | Upload limit-zone GeoJSON | OK |
| UpdatePlacementDates | /CityPlanning/Admin/UpdatePlacementDates | POST | Update placement window dates | OK |
| DownloadLimitZone | /CityPlanning/Admin/DownloadLimitZone | GET | Download stored limit-zone GeoJSON | OK |
| DeleteLimitZone | /CityPlanning/Admin/DeleteLimitZone | POST | Remove the limit-zone polygon | OK |
| UploadOfficialZones | /CityPlanning/Admin/UploadOfficialZones | POST | Upload official zones GeoJSON | OK |
| DownloadOfficialZones | /CityPlanning/Admin/DownloadOfficialZones | GET | Download stored official zones GeoJSON | OK |
| DeleteOfficialZones | /CityPlanning/Admin/DeleteOfficialZones | POST | Remove official zones | OK |

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

## ContactsController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Contacts | GET | Admin contacts list (search) | OK |
| Detail | /Contacts/{id} | GET | Contact detail | OK |
| Create | /Contacts/Create | GET | Create contact form | OK |
| Create | /Contacts/Create | POST | Submit new contact | OK |

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
| SeedDashboard | /dev/seed/dashboard | POST | Seed sample shift-dashboard data (non-prod) | OK |

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

## GoogleController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Google | GET | Google integration admin landing | OK |
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
| CheckEmailMismatches | /Google/CheckEmailMismatches | POST | Check email mismatches | OK |
| EmailBackfillReview | /Google/EmailBackfillReview | GET | Review email backfill results | OK |
| ApplyEmailBackfill | /Google/ApplyEmailBackfill | POST | Apply email corrections | OK |
| Sync | /Google/Sync | GET | Google sync status page | OK |
| SyncPreview | /Google/Sync/Preview/{resourceType} | GET | Preview sync (JSON) | OK |
| SyncExecute | /Google/Sync/Execute/{resourceId} | POST | Execute sync for a resource (JSON) | OK |
| SyncExecuteAll | /Google/Sync/ExecuteAll/{resourceType} | POST | Execute sync for all resources of type (JSON) | OK |
| CheckDriveActivity | /Google/AuditLog/CheckDriveActivity | POST | Trigger manual Drive activity check | OK |
| GoogleSyncResourceAudit | /Google/Sync/Resource/{id}/Audit | GET | Audit log for a Google resource | → `ResourceAudit` (the `Google` prefix is redundant on `GoogleController`) |
| HumanGoogleSyncAudit | /Google/Human/{id}/SyncAudit | GET | Google sync audit for a human | → `HumanSyncAudit` (the `Google` prefix is redundant on `GoogleController`) |
| ProvisionEmail | /Google/Human/{id}/ProvisionEmail | POST | Provision @nobodies.team email for a human | OK |
| Accounts | /Google/Accounts | GET | List @nobodies.team workspace accounts | OK |
| ProvisionAccount | /Google/Accounts/Provision | POST | Provision new workspace account | OK |
| SuspendAccount | /Google/Accounts/Suspend | POST | Suspend a workspace account | OK |
| ReactivateAccount | /Google/Accounts/Reactivate | POST | Reactivate a workspace account | OK |
| ResetPassword | /Google/Accounts/ResetPassword | POST | Reset workspace account password | OK |
| LinkAccount | /Google/Accounts/Link | POST | Link workspace email to a human | OK |
| SyncOutbox | /Google/SyncOutbox | GET | View Google sync outbox events | OK |
| CheckEmailRenames | /Google/CheckEmailRenames | POST | Detect renamed Workspace emails | OK |
| EmailRenames | /Google/EmailRenames | GET | View detected email renames | OK |
| FixEmailRename | /Google/FixEmailRename | POST | Apply a detected email rename | OK |

## GovernanceController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Governance | GET | Governance info page (statutes, tier info) | OK |
| Roles | /Governance/Roles | GET | Role assignments list (Board/Admin) | OK |

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

## NotificationController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Notifications | GET | Notification inbox | OK |
| GetPopup | /Notifications/Popup | GET | Notification popup partial | OK |
| Resolve | /Notifications/Resolve/{id} | POST | Mark notification resolved | OK |
| Dismiss | /Notifications/Dismiss/{id} | POST | Dismiss a notification | OK |
| MarkRead | /Notifications/MarkRead/{id} | POST | Mark a notification read | OK |
| MarkAllRead | /Notifications/MarkAllRead | POST | Mark all notifications read | OK |
| BulkResolve | /Notifications/BulkResolve | POST | Bulk-resolve selected notifications | OK |
| BulkDismiss | /Notifications/BulkDismiss | POST | Bulk-dismiss selected notifications | OK |
| ClickThrough | /Notifications/ClickThrough/{id} | GET | Mark read and redirect to notification target | OK |

## OnboardingReviewController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /OnboardingReview | GET | Onboarding review queue | OK |
| Detail | /OnboardingReview/{userId} | GET | Review detail for a human | OK |
| Clear | /OnboardingReview/{userId}/Clear | POST | Clear consent check | OK |
| Flag | /OnboardingReview/{userId}/Flag | POST | Flag consent check | OK |
| Reject | /OnboardingReview/{userId}/Reject | POST | Reject a signup | OK |
| BoardVoting | /OnboardingReview/BoardVoting | GET | Board voting dashboard | OK |
| BoardVotingDetail | /OnboardingReview/BoardVoting/{applicationId} | GET | Board voting detail | OK |
| Vote | /OnboardingReview/BoardVoting/Vote | POST | Cast a board vote | OK |
| Finalize | /OnboardingReview/BoardVoting/Finalize | POST | Finalize application decision | OK |

## ProfileApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Search | /api/profiles/search | GET | Profile autocomplete API | OK |

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
| SetNotificationTarget | /Profile/Me/Emails/SetNotificationTarget | POST | Set primary notification email | OK |
| SetEmailVisibility | /Profile/Me/Emails/SetVisibility | POST | Change email visibility | OK |
| DeleteEmail | /Profile/Me/Emails/Delete | POST | Remove an email | OK |
| SetGoogleServiceEmail | /Profile/Me/Emails/SetGoogleService | POST | Set Google service email | OK |
| MyOutbox | /Profile/Me/Outbox | GET | View own email outbox | OK |
| Privacy | /Profile/Me/Privacy | GET | Privacy & data management page | → `DataPrivacy` ? (overlaps with `HomeController.Privacy`; this is the user's GDPR page, not the public privacy policy) |
| RequestDeletion | /Profile/Me/Privacy/RequestDeletion | POST | Request account deletion | OK |
| CancelDeletion | /Profile/Me/Privacy/CancelDeletion | POST | Cancel pending deletion | OK |
| ShiftInfo | /Profile/Me/ShiftInfo | GET | Shift profile info form | OK |
| ShiftInfo | /Profile/Me/ShiftInfo | POST | Submit shift profile info | OK |
| CommunicationPreferences | /Profile/Me/CommunicationPreferences | GET | Communication preferences page | OK |
| UpdatePreference | /Profile/Me/CommunicationPreferences/Update | POST | Update a single comms preference | OK |
| Notifications | /Profile/Me/Notifications | GET | Permanent redirect to CommunicationPreferences | OK |
| DownloadData | /Profile/Me/DownloadData | GET | GDPR data export | OK |
| Picture | /Profile/Picture | GET | Serve custom profile picture | OK |
| ViewProfile | /Profile/{id} | GET | Public profile page for a human | OK |
| Popover | /Profile/{id}/Popover | GET | Mini profile popover (partial) | OK |
| SendMessage | /Profile/{id}/SendMessage | GET | Facilitated message form | OK |
| SendMessage | /Profile/{id}/SendMessage | POST | Send facilitated message | OK |
| Search | /Profile/Search | GET | Human search page | OK |
| AdminList | /Profile/Admin | GET | Admin: human list with filters | OK |
| AdminDetail | /Profile/{id}/Admin | GET | Admin: human detail page | OK |
| AdminOutbox | /Profile/{id}/Admin/Outbox | GET | Admin: email outbox for a human | OK |
| SuspendHuman | /Profile/{id}/Admin/Suspend | POST | Suspend a human | OK |
| UnsuspendHuman | /Profile/{id}/Admin/Unsuspend | POST | Unsuspend a human | OK |
| ApproveVolunteer | /Profile/{id}/Admin/Approve | POST | Approve volunteer onboarding | OK |
| RejectSignup | /Profile/{id}/Admin/Reject | POST | Reject a signup | OK |
| AddRole | /Profile/{id}/Admin/Roles/Add | GET | Add role form | OK |
| AddRole | /Profile/{id}/Admin/Roles/Add | POST | Submit role assignment | OK |
| EndRole | /Profile/{id}/Admin/Roles/{roleId}/End | POST | End a role assignment | OK |

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

## VolController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Vol | GET | Redirect to MyShifts | OK |
| MyShifts | /Vol/MyShifts | GET | Volunteer's shift list | OK |
| Bail | /Vol/Bail | POST | Bail from a shift | OK |
| Shifts | /Vol/Shifts | GET | Browse all shifts | OK |
| SignUp | /Vol/SignUp | POST | Sign up for a shift | OK |
| Teams | /Vol/Teams | GET | Department/teams overview | OK |
| DepartmentDetail | /Vol/Teams/{slug} | GET | Department detail page | OK |
| ChildTeamDetail | /Vol/Teams/{parentSlug}/{childSlug} | GET | Child team detail page | OK |
| Approve | /Vol/Approve | POST | Approve a signup | OK |
| Refuse | /Vol/Refuse | POST | Refuse a signup | OK |
| NoShow | /Vol/NoShow | POST | Mark no-show | OK |
| ApproveJoinRequest | /Vol/ApproveJoinRequest | POST | Approve team join request | OK |
| RejectJoinRequest | /Vol/RejectJoinRequest | POST | Reject team join request | OK |
| Urgent | /Vol/Urgent | GET | Urgent shifts dashboard | OK |
| Voluntell | /Vol/Voluntell | POST | Assign volunteer to shift | OK |
| Management | /Vol/Management | GET | Management dashboard | OK |
| Settings | /Vol/Settings | GET | Event settings (Admin) | OK |
| Settings | /Vol/Settings | POST | Save event settings (Admin) | OK |
| SearchVolunteers | /Vol/SearchVolunteers | GET | Search volunteers (JSON) | OK |

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
| ApplicationController | `Applications` | `AdminList` ? | On `ApplicationController`, "Applications" reads as a duplicate of `Index` — this is the admin filtered list |
| EmailController | `EmailOutbox` / `RetryEmailOutboxMessage` / `DiscardEmailOutboxMessage` / `EmailPreview` | `Outbox` / `RetryOutboxMessage` / `DiscardOutboxMessage` / `Preview` | The `Email` prefix duplicates the controller name |
| GoogleController | `GoogleSyncResourceAudit` | `ResourceAudit` | The `Google` prefix duplicates the controller name |
| GoogleController | `HumanGoogleSyncAudit` | `HumanSyncAudit` | The `Google` prefix duplicates the controller name |
| ProfileController | `Privacy` | `DataPrivacy` ? | Avoids overlap with `HomeController.Privacy` (site policy vs user GDPR page) |
| UnsubscribeController | `Index` | `Landing` ? | This isn't a list page — it's a token-specific landing/redirect |

**Note:** Items marked with `?` are suggestions where the rename benefit is marginal — worth discussing but not critical.

**High-confidence renames (no `?`):**
1. `GoogleController.GoogleSyncResourceAudit` → `ResourceAudit` — redundant prefix
2. `GoogleController.HumanGoogleSyncAudit` → `HumanSyncAudit` — redundant prefix
3. `EmailController.EmailOutbox` → `Outbox` (and matching peers) — redundant prefix

All other actions have names that adequately describe what the user sees or what the action does, given their route context.

---

## Part 2: Misplaced Actions & Ideal Controller Breakdown

### Status of the original (#261) splits

Several of the splits proposed in the original audit have since shipped:

- **HumanController has been merged into `ProfileController`** — `View`/`Popover`/`SendMessage`, the admin actions (`AdminList`, `AdminDetail`, `AdminOutbox`, `SuspendHuman`, `UnsuspendHuman`, `ApproveVolunteer`, `RejectSignup`, `AddRole`, `EndRole`), and even the `Search` page now live on `/Profile`. The `View` → `HumanProfile` rename was implemented as `ViewProfile`. The `HumanGoogleSyncAudit` action moved to `GoogleController` (still carries the redundant `Human`/`Google` prefixes — see Part 1).
- **ContactsController** is now its own controller (`/Contacts`), extracted from the old `HumanController`.
- **GoogleController** absorbed all sync, workspace-account, and email-rename actions previously spread across `AdminController`, `BoardController`, `AdminEmailController`, and the team-controller `Sync*` actions.
- **EmailController** is now its own controller (`/Email`) holding the email outbox + preview that previously lived on `AdminController`.
- **AdminDuplicateAccountsController** is new and handles the duplicate-account workflow.
- **CalendarController** and **CityPlanningController** / **CityPlanningApiController** are new sections.
- **GuestController** owns the profileless-account dashboard, with its own GDPR / comms-prefs actions parallel to `ProfileController`.
- **NotificationController** is its own section.

### Remaining misplaced actions

#### TeamController — community features still on a team controller (17 actions)

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Birthdays` | TeamController | Community birthday calendar — not team-specific | **CommunityController** (`/Community/Birthdays`) |
| `Map` | TeamController | Member location map — not team-specific | **CommunityController** (`/Community/Map`) |
| `Roster` | TeamController | Shift roster — belongs with shift domain | **ShiftsController** or **VolController** |
| `Summary`, `CreateTeam`, `EditTeam`, `DeleteTeam` | TeamController | Admin team CRUD | **TeamAdminController** already exists — move these there (route: `/Teams/Admin/...`) |

#### ApplicationController — user + admin on one controller

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Index`, `Create`, `Details`, `Withdraw` | ApplicationController | User-facing — fine here | Stay |
| `Applications`, `ApplicationDetail` | ApplicationController | Admin filtered list + admin detail — different audience, different auth | **ApplicationAdminController** or fold into **OnboardingReviewController** (which already handles the board voting side of the same workflow) |

#### OnboardingReviewController — two workflows in one (9 actions)

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Index`, `Detail`, `Clear`, `Flag`, `Reject` | OnboardingReviewController | Consent review queue | Stay |
| `BoardVoting`, `BoardVotingDetail`, `Vote`, `Finalize` | OnboardingReviewController | Board voting on tier applications — conceptually distinct from consent review | **BoardVotingController** (`/Board/Voting/...`) |

#### ProfileController — five concerns in one (~37 actions)

The profile controller has grown — it now owns own-profile, email, privacy, shift info, comms prefs, public viewing, and the entire human-admin surface.

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Index`, `Me`, `Edit` (GET+POST), `Picture`, `ShiftInfo`, `ViewProfile`, `Popover`, `SendMessage`, `Search` | ProfileController | Core profile + public viewing — fine here | Stay |
| `Emails`, `AddEmail`, `VerifyEmail`, `SetNotificationTarget`, `SetEmailVisibility`, `DeleteEmail`, `SetGoogleServiceEmail` | ProfileController | Email management — 7 actions, own sub-domain | **ProfileEmailController** (`/Profile/Me/Emails/...`) |
| `Privacy`, `RequestDeletion`, `CancelDeletion`, `DownloadData`, `MyOutbox` | ProfileController | GDPR/data rights | **ProfilePrivacyController** (`/Profile/Me/Privacy/...`) |
| `CommunicationPreferences`, `UpdatePreference`, `Notifications` | ProfileController | Communication prefs | Could stay or move to email controller |
| `AdminList`, `AdminDetail`, `AdminOutbox`, `SuspendHuman`, `UnsuspendHuman`, `ApproveVolunteer`, `RejectSignup`, `AddRole` (GET+POST), `EndRole` | ProfileController | Admin human management — 10 actions behind `[Authorize(Policy = HumanAdminBoardOrAdmin)]` overrides | **HumanAdminController** (`/Profile/Admin/...` or `/Humans/Admin/...`) |

#### ShiftsController — admin settings mixed with user browsing

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Index`, `SignUp`, `SignUpRange`, `Bail`, `BailRange`, `Mine`, `SaveAvailability`, `RegenerateIcal`, `SaveTagPreferences` | ShiftsController | User-facing shift browsing | Stay |
| `Settings` (GET+POST) | ShiftsController | Admin-only event settings | **ShiftDashboardController** or a dedicated **EventSettingsController** |

#### GovernanceController — admin action on user page

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Index` | GovernanceController | Governance info page | Stay |
| `Roles` | GovernanceController | Admin role assignment list (Board/Admin only) | Move to a dedicated **RoleAdminController** or under `/Profile/Admin/Roles` |

### VolController — intentional duplication

VolController (~19 actions) substantially duplicates ShiftsController, ShiftAdminController, and ShiftDashboardController. It's a mobile-first redesign of the same shift domain. This isn't "misplaced" — it's a parallel UI — but worth flagging since the two UIs will drift unless there's a deliberate strategy for which one is canonical.

---

### Priority Ranking for Splits

If tackling this incrementally, ordered by impact:

1. **ProfileController → HumanAdminController** — highest impact, ~10 admin actions on a user-profile controller; clear `[Authorize]`-policy boundary makes the split mechanical.
2. **ProfileController → ProfileEmailController + ProfilePrivacyController** — ~12 actions across two clear sub-domains.
3. **TeamController → TeamManagementController + CommunityController** — community features (`Birthdays`, `Map`, `Roster`) hiding on a team controller; admin team CRUD belongs on `TeamAdminController`.
4. **OnboardingReviewController → BoardVotingController** — clean conceptual split, two distinct workflows.
5. **ApplicationController → ApplicationAdminController** — small but clean split.
6. **ShiftsController → EventSettingsController** — minor, just 2 actions.
7. **GovernanceController → RoleAdminController** — minor, just 1 action.
