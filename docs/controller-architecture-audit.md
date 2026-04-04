# Controller Architecture Audit

Living document. Last updated: 2026-03-29 (#261 Phase 1).

## Part 1: Action Name Audit

### Summary
- Controllers audited: 35 (excludes 2 abstract base classes and 1 base camp controller)
- Actions audited: 258
- Renames suggested: 23
- Already OK: 235

---

## AccountController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Login | /Account/Login | GET | Login page | OK |
| ExternalLogin | /Account/ExternalLogin | POST | Initiate Google OAuth | OK |
| ExternalLoginCallback | /Account/ExternalLoginCallback | GET | Handle OAuth callback | OK |
| MagicLinkRequest | /Account/MagicLinkRequest | POST | Request magic link email | OK |
| MagicLinkConfirm | /Account/MagicLinkConfirm | GET | Magic link landing page | OK |
| MagicLink | /Account/MagicLink | POST | Verify magic link token and sign in | OK |
| MagicLinkSignup | /Account/MagicLinkSignup | GET | New user signup via magic link | OK |
| CompleteSignup | /Account/CompleteSignup | POST | Finalize magic link signup | OK |
| Logout | /Account/Logout | POST | Sign out | OK |
| AccessDenied | /Account/AccessDenied | GET | Access denied page | OK |

## AdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Admin | GET | Admin dashboard | OK |
| PurgeHuman | /Admin/Humans/{id}/Purge | POST | Purge a human (non-prod) | OK |
| SyncSystemTeams | /Admin/SyncSystemTeams | POST | Trigger system team sync | OK |
| SyncResults | /Admin/SyncResults | GET | View sync results | OK |
| Logs | /Admin/Logs | GET | View in-memory logs | OK |
| Configuration | /Admin/Configuration | GET | View configuration status | OK |
| EmailPreview | /Admin/EmailPreview | GET | Preview all email templates | OK |
| SyncSettings | /Admin/SyncSettings | GET | View sync service settings | OK |
| UpdateSyncSetting | /Admin/SyncSettings | POST | Update a sync service mode | OK |
| DbVersion | /Admin/DbVersion | GET | Database migration info (anonymous) | OK |
| EmailOutbox | /Admin/EmailOutbox | GET | View email outbox queue | OK |
| PauseEmailSending | /Admin/EmailOutbox/Pause | POST | Pause email sending | OK |
| ResumeEmailSending | /Admin/EmailOutbox/Resume | POST | Resume email sending | OK |
| RetryEmailOutboxMessage | /Admin/EmailOutbox/Retry/{id} | POST | Retry a failed email | OK |
| DiscardEmailOutboxMessage | /Admin/EmailOutbox/Discard/{id} | POST | Discard a queued email | OK |
| ClearHangfireLocks | /Admin/ClearHangfireLocks | POST | Clear stale Hangfire locks | OK |
| CheckGroupSettings | /Admin/CheckGroupSettings | POST | Check Google Group settings drift | OK |
| GroupSettingsResults | /Admin/GroupSettingsResults | GET | View group settings check results | OK |
| CheckEmailMismatches | /Admin/CheckEmailMismatches | POST | Check email mismatches | OK |
| EmailBackfillReview | /Admin/EmailBackfillReview | GET | Review email backfill results | OK |
| ApplyEmailBackfill | /Admin/ApplyEmailBackfill | POST | Apply email corrections | OK |
| AllGroups | /Admin/AllGroups | GET | List all domain groups | OK |
| RemediateGroupSettings | /Admin/RemediateGroupSettings | POST | Remediate one group's settings | OK |
| RemediateAllGroupSettings | /Admin/RemediateAllGroupSettings | POST | Remediate all drifted groups | OK |
| LinkGroupToTeam | /Admin/LinkGroupToTeam | POST | Link a Google Group to a team | OK |

## AdminEmailController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Admin/Email | GET | List @nobodies.team accounts | OK |
| Provision | /Admin/Email/Provision | POST | Provision new workspace account | OK |
| Suspend | /Admin/Email/Suspend | POST | Suspend a workspace account | OK |
| Reactivate | /Admin/Email/Reactivate | POST | Reactivate a workspace account | OK |
| ResetPassword | /Admin/Email/ResetPassword | POST | Reset workspace account password | OK |
| Link | /Admin/Email/Link | POST | Link workspace email to a human | OK |

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
| Applications | /Application/Admin | GET | Admin: filtered applications list | → `AdminList` ? (generic "Applications" on ApplicationController is confusing — this is the admin list view, not the user-facing one) |
| ApplicationDetail | /Application/Admin/{id} | GET | Admin: application detail with voting | OK |

## BoardController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Board | GET | Board dashboard with stats | OK |
| AuditLog | /Board/AuditLog | GET | Board audit log | OK |
| CheckDriveActivity | /Board/AuditLog/CheckDriveActivity | POST | Trigger manual Drive activity check | OK |
| GoogleSyncResourceAudit | /Board/GoogleSync/Resource/{id}/Audit | GET | Audit log for a Google resource | OK |

## BudgetController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Budget | GET | Coordinator budget view | OK |
| Summary | /Budget/Summary | GET | Public budget summary | OK |
| CategoryDetail | /Budget/Category/{id} | GET | Budget category detail | OK |
| CreateLineItem | /Budget/LineItems/Create | POST | Create a line item | OK |
| UpdateLineItem | /Budget/LineItems/{id}/Update | POST | Update a line item | OK |
| DeleteLineItem | /Budget/LineItems/{id}/Delete | POST | Delete a line item | OK |

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
| Delete | /Camps/Admin/Delete | POST | Delete a camp (Admin only) | OK |

## CampApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| GetCamps | /api/camps/{year} | GET | Public camp summaries for a year | OK |
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

## ConsentController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Consent | GET | Consent dashboard | OK |
| Review | /Consent/Review | GET | Review a document before consenting | OK |
| Submit | /Consent/Submit | POST | Submit consent | OK |

## DevLoginController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| SignIn | /dev/login/{persona} | GET | Sign in as dev persona | OK |
| Users | /dev/login/users | GET | List real users for impersonation | OK |
| SignInAsUser | /dev/login/users/{id} | GET | Sign in as any user | OK |

## FeedbackApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| List | /api/feedback | GET | List feedback reports (API) | OK |
| Get | /api/feedback/{id} | GET | Get single feedback report (API) | OK |
| GetMessages | /api/feedback/{id}/messages | GET | Get messages for a report (API) | OK |
| PostMessage | /api/feedback/{id}/messages | POST | Post admin message (API) | OK |
| UpdateStatus | /api/feedback/{id}/status | PATCH | Update feedback status (API) | OK |
| SetGitHubIssue | /api/feedback/{id}/github-issue | PATCH | Link GitHub issue (API) | OK |

## FeedbackController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Feedback | GET | Feedback list page | OK |
| Detail | /Feedback/{id} | GET | Feedback detail (AJAX partial or redirect) | OK |
| Submit | /Feedback | POST | Submit new feedback | OK |
| PostMessage | /Feedback/{id}/Message | POST | Post message on feedback thread | OK |
| UpdateStatus | /Feedback/{id}/Status | POST | Update feedback status | OK |
| SetGitHubIssue | /Feedback/{id}/GitHubIssue | POST | Link GitHub issue to feedback | OK |

## FinanceController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Finance | GET | Finance home (active year or no-year) | OK |
| YearDetail | /Finance/Years/{id} | GET | Budget year detail | OK |
| CategoryDetail | /Finance/Categories/{id} | GET | Budget category detail | OK |
| AuditLog | /Finance/AuditLog/{yearId?} | GET | Budget audit log | OK |
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

## GovernanceController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Governance | GET | Governance info page (statutes, tier info) | OK |
| Roles | /Governance/Roles | GET | Role assignments list (Board/Admin) | OK |

## HomeController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | / | GET | Landing page or authenticated dashboard | OK |
| Privacy | /Home/Privacy | GET | Privacy policy page | OK |
| About | /Home/About | GET | About page with license info | OK |
| Error | /Home/Error/{statusCode?} | GET | Error page | OK |

## HumanApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Search | /api/humans/search | GET | Search humans (autocomplete API) | OK |

## HumanController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| View | /Human/{id} | GET | View a human's profile | → `HumanProfile` (View is too generic — this shows a human's public profile page) |
| Popover | /Human/{id}/Popover | GET | Mini profile popover (partial) | OK |
| SendMessage | /Human/{id}/SendMessage | GET | Facilitated message form | OK |
| SendMessage | /Human/{id}/SendMessage | POST | Send facilitated message | OK |
| Humans | /Human/Admin | GET | Admin: human list | → `HumanList` ? (plural "Humans" reads oddly as a method name on HumanController) |
| HumanDetail | /Human/{id}/Admin | GET | Admin: human detail page | OK |
| ProvisionEmail | /Human/{id}/ProvisionEmail | POST | Provision @nobodies.team email | OK |
| Outbox | /Human/{id}/Outbox | GET | View email outbox for a human | OK |
| SuspendHuman | /Human/{id}/Admin/Suspend | POST | Suspend a human | OK |
| UnsuspendHuman | /Human/{id}/Admin/Unsuspend | POST | Unsuspend a human | OK |
| ApproveVolunteer | /Human/{id}/Admin/Approve | POST | Approve volunteer onboarding | OK |
| RejectSignup | /Human/{id}/Admin/Reject | POST | Reject a signup | OK |
| HumanGoogleSyncAudit | /Human/{id}/Admin/GoogleSyncAudit | GET | Google sync audit for a human | → `GoogleSyncAudit` (the "Human" prefix is redundant on HumanController) |
| AddRole | /Human/{id}/Admin/Roles/Add | GET | Add role form | OK |
| AddRole | /Human/{id}/Admin/Roles/Add | POST | Submit role assignment | OK |
| EndRole | /Human/{id}/Admin/Roles/{roleId}/End | POST | End a role assignment | OK |
| Contacts | /Human/Admin/Contacts | GET | Admin: contact list | OK |
| ContactDetail | /Human/Admin/Contacts/{id} | GET | Admin: contact detail | OK |
| CreateContact | /Human/Admin/Contacts/Create | GET | Create contact form | OK |
| CreateContact | /Human/Admin/Contacts/Create | POST | Submit new contact | OK |

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

## ProfileController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Profile | GET | Own profile page | OK |
| Edit | /Profile/Edit | GET | Edit own profile form | OK |
| Edit | /Profile/Edit | POST | Submit profile edits | OK |
| Picture | /Profile/Picture | GET | Serve custom profile picture | OK |
| Emails | /Profile/Emails | GET | Email management page | OK |
| AddEmail | /Profile/AddEmail | POST | Add a new email | OK |
| VerifyEmail | /Profile/VerifyEmail | GET | Verify email via token | OK |
| SetNotificationTarget | /Profile/SetNotificationTarget | POST | Set primary notification email | OK |
| SetEmailVisibility | /Profile/SetEmailVisibility | POST | Change email visibility | OK |
| DeleteEmail | /Profile/DeleteEmail | POST | Remove an email | OK |
| SetGoogleServiceEmail | /Profile/SetGoogleServiceEmail | POST | Set Google service email | OK |
| Outbox | /Profile/Outbox | GET | View own email outbox | OK |
| Privacy | /Profile/Privacy | GET | Privacy & data management page | → `DataPrivacy` ? (conflicts with HomeController.Privacy conceptually — this is the user's GDPR data page, not the site's privacy policy) |
| RequestDeletion | /Profile/RequestDeletion | POST | Request account deletion | OK |
| CancelDeletion | /Profile/CancelDeletion | POST | Cancel pending deletion | OK |
| ShiftInfo | /Profile/ShiftInfo | GET | Shift profile info form | OK |
| ShiftInfo | /Profile/ShiftInfo | POST | Submit shift profile info | OK |
| Notifications | /Profile/Notifications | GET | Communication preferences page | OK |
| Notifications | /Profile/Notifications | POST | Update communication preferences | OK |
| DownloadData | /Profile/DownloadData | GET | GDPR data export | OK |

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
| Settings | /Shifts/Settings | GET | Event settings form (Admin) | OK |
| Settings | /Shifts/Settings | POST | Save event settings (Admin) | OK |

## TeamAdminController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| ApproveRequest | /Teams/{slug}/Requests/{requestId}/Approve | POST | Approve join request | OK |
| RejectRequest | /Teams/{slug}/Requests/{requestId}/Reject | POST | Reject join request | OK |
| Members | /Teams/{slug}/Members | GET | Team member list with admin actions | OK |
| RemoveMember | /Teams/{slug}/Members/{userId}/Remove | POST | Remove a member | OK |
| AddMember | /Teams/{slug}/Members/Add | POST | Add a member directly | OK |
| SearchUsers | /Teams/{slug}/Members/Search | GET | Search users to add (JSON) | OK |
| Resources | /Teams/{slug}/Resources | GET | Team Google resources page | OK |
| LinkDriveResource | /Teams/{slug}/Resources/LinkDrive | POST | Link a Drive resource | OK |
| LinkGroup | /Teams/{slug}/Resources/LinkGroup | POST | Link a Google Group | OK |
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
| Search | /Teams/Search | GET | Human search page | → `HumanSearch` ? (on TeamController, "Search" is ambiguous — could be teams or humans) |
| Roster | /Teams/Roster | GET | Shift roster overview | OK |
| Map | /Teams/Map | GET | Member map | OK |
| MyTeams | /Teams/My | GET | Current user's teams | OK |
| Join | /Teams/{slug}/Join | GET | Join team form | OK |
| Join | /Teams/{slug}/Join | POST | Submit join request | OK |
| Leave | /Teams/{slug}/Leave | POST | Leave a team | OK |
| WithdrawRequest | /Teams/Requests/{id}/Withdraw | POST | Withdraw a join request | OK |
| Sync | /Teams/Sync | GET | Google sync status page | OK |
| SyncPreview | /Teams/Sync/Preview/{resourceType} | GET | Preview sync (JSON) | OK |
| SyncExecute | /Teams/Sync/Execute/{resourceId} | POST | Execute sync for a resource (JSON) | OK |
| SyncExecuteAll | /Teams/Sync/ExecuteAll/{resourceType} | POST | Execute sync for all resources of type (JSON) | OK |
| Summary | /Teams/Summary | GET | Admin: team summary list | OK |
| CreateTeam | /Teams/Create | GET | Create team form | OK |
| CreateTeam | /Teams/Create | POST | Submit new team | OK |
| EditTeam | /Teams/{id}/Edit | GET | Edit team form | OK |
| EditTeam | /Teams/{id}/Edit | POST | Submit team edits | OK |
| DeleteTeam | /Teams/{id}/Delete | POST | Deactivate a team | OK |

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
| ExportAttendees | /Tickets/Export/Attendees | GET | Export attendees as CSV | OK |
| ExportOrders | /Tickets/Export/Orders | GET | Export orders as CSV | OK |

## TimezoneApiController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| SetTimezone | /api/timezone | POST | Set user timezone in session | OK |

## UnsubscribeController

| Method | Route | Verb | Purpose | Suggestion |
|--------|-------|------|---------|------------|
| Index | /Unsubscribe/{token} | GET | Unsubscribe confirmation page | → `Confirm` or `UnsubscribeLanding` ? (Index is misleading — this is a token-specific landing, not a list) |
| Confirm | /Unsubscribe/{token} | POST | Execute unsubscribe | OK |
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
| HumanController | `View` | `HumanProfile` | "View" is too generic — this is the public profile page for a human |
| HumanController | `Humans` | `HumanList` ? | Plural noun reads oddly as a method; "HumanList" describes the admin list page |
| HumanController | `HumanGoogleSyncAudit` | `GoogleSyncAudit` | "Human" prefix is redundant on HumanController |
| ApplicationController | `Applications` | `AdminList` ? | On ApplicationController, "Applications" is ambiguous with user-facing `Index` — this is the admin filtered list |
| TeamController | `Search` | `HumanSearch` ? | On TeamController, "Search" is ambiguous — this searches humans, not teams |
| UnsubscribeController | `Index` | `UnsubscribeLanding` ? | This isn't a list page — it's a token-specific confirmation landing |
| ProfileController | `Privacy` | `DataPrivacy` ? | Avoids confusion with HomeController.Privacy (site policy vs user GDPR page) |

**Note:** Items marked with `?` are suggestions where the rename benefit is marginal — worth discussing but not critical.

**High confidence renames (no `?`):**
1. `HumanController.View` → `HumanProfile` — the original issue's known-bad example
2. `HumanController.HumanGoogleSyncAudit` → `GoogleSyncAudit` — redundant prefix

All other actions have names that adequately describe what the user sees or what the action does, given their route context.

---

## Part 2: Misplaced Actions & Ideal Controller Breakdown

### Problem: Mixed Concerns

Several controllers mix user-facing and admin functionality, or host actions that belong to a different domain concept. This creates large files, confusing `[Authorize]` layering (class-level vs action-level overrides), and makes it harder to reason about which controller owns what.

### Misplaced Actions

#### HumanController — the biggest offender (21 actions, 850 lines)

Mixes three distinct audiences on one controller:

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `View`, `Popover`, `SendMessage` (GET+POST) | HumanController | Public/user-facing profile viewing | Stay — these are the core of HumanController |
| `Humans`, `HumanDetail`, `SuspendHuman`, `UnsuspendHuman`, `ApproveVolunteer`, `RejectSignup`, `ProvisionEmail`, `Outbox`, `HumanGoogleSyncAudit`, `AddRole` (GET+POST), `EndRole` | HumanController | Admin human management — 14 actions behind `[Authorize(Roles = ...)]` overrides | **HumanAdminController** (`/Human/Admin/...`) |
| `Contacts`, `ContactDetail`, `CreateContact` (GET+POST) | HumanController | Contact management — entirely separate domain entity | **ContactController** (`/Contacts/...`) or **ContactAdminController** |

#### TeamController — community features hiding on a team controller (26 actions, 825 lines)

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Search` | TeamController | Searches humans, not teams | **HumanController** or a dedicated **SearchController** |
| `Birthdays` | TeamController | Community birthday calendar — not team-specific | **CommunityController** (`/Community/Birthdays`) |
| `Map` | TeamController | Member location map — not team-specific | **CommunityController** (`/Community/Map`) |
| `Roster` | TeamController | Shift roster — belongs with shift domain | **ShiftsController** or **VolController** |
| `Summary`, `CreateTeam`, `EditTeam`, `DeleteTeam` | TeamController | Admin team CRUD | **TeamAdminController** already exists — move these there (route: `/Teams/Admin/...`) |
| `Sync`, `SyncPreview`, `SyncExecute`, `SyncExecuteAll` | TeamController | Google sync operations | **SyncController** (`/Admin/Sync/...`) or **TeamSyncController** |

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

#### ProfileController — five concerns in one (20 actions)

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Index`, `Edit` (GET+POST), `Picture` | ProfileController | Core profile — fine here | Stay |
| `Emails`, `AddEmail`, `VerifyEmail`, `SetNotificationTarget`, `SetEmailVisibility`, `DeleteEmail`, `SetGoogleServiceEmail` | ProfileController | Email management — 7 actions, own sub-domain | **ProfileEmailController** (`/Profile/Emails/...`) |
| `Privacy`, `RequestDeletion`, `CancelDeletion`, `DownloadData` | ProfileController | GDPR/data rights | **ProfilePrivacyController** (`/Profile/Privacy/...`) |
| `ShiftInfo` (GET+POST) | ProfileController | Shift volunteer profile | Could stay or move to `/Vol/Profile` |
| `Notifications` (GET+POST), `Outbox` | ProfileController | Communication prefs + outbox | Could stay or move to email controller |

#### ShiftsController — admin settings mixed with user browsing

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Index`, `SignUp`, `SignUpRange`, `Bail`, `BailRange`, `Mine`, `SaveAvailability`, `RegenerateIcal` | ShiftsController | User-facing shift browsing | Stay |
| `Settings` (GET+POST) | ShiftsController | Admin-only event settings | **ShiftDashboardController** or a dedicated **EventSettingsController** |

#### GovernanceController — admin action on user page

| Action Group | Current Location | Problem | Better Home |
|-------------|-----------------|---------|------------|
| `Index` | GovernanceController | Governance info page | Stay |
| `Roles` | GovernanceController | Admin role assignment list (Board/Admin only) | Move to **HumanAdminController** or a dedicated **RoleAdminController** |

### VolController — intentional duplication?

VolController (19 actions, 870 lines) substantially duplicates ShiftsController, ShiftAdminController, and ShiftDashboardController. It's a mobile-first redesign of the same shift domain. This isn't "misplaced" — it's a parallel UI — but worth flagging since the two UIs will drift unless there's a deliberate strategy for which one is canonical.

---

### Ideal Controller Breakdown (from scratch)

If starting fresh, here's how the 258 actions would group across ~40 focused controllers:

#### Auth & Account
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **AccountController** | /Account | Login, OAuth, MagicLink, Logout, AccessDenied | Keep as-is |
| **DevLoginController** | /dev/login | Dev personas, user impersonation | Keep as-is |

#### Home & Public
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **HomeController** | / | Index/Dashboard, Privacy, About, Error | Keep as-is |
| **LegalController** | /Legal | Public legal documents | Keep as-is |
| **UnsubscribeController** | /Unsubscribe | Unsubscribe landing + confirm | Keep as-is |
| **LanguageController** | /Language | SetLanguage | Keep as-is |

#### Profile (own user)
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **ProfileController** | /Profile | Index, Edit, Picture, ShiftInfo | Core profile only |
| **ProfileEmailController** | /Profile/Emails | List, Add, Verify, SetTarget, Visibility, Delete, GoogleService | **New** — extracted from ProfileController |
| **ProfilePrivacyController** | /Profile/Privacy | DataPrivacy, RequestDeletion, CancelDeletion, DownloadData, Outbox | **New** — GDPR concerns |
| **ProfileNotificationsController** | /Profile/Notifications | Preferences GET+POST | **New** — could stay on ProfileController if too granular |

#### Humans (viewing + admin)
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **HumanController** | /Human | HumanProfile, Popover, SendMessage, Search | Public profile viewing |
| **HumanAdminController** | /Human/Admin | List, Detail, Suspend, Unsuspend, Approve, Reject, ProvisionEmail, Outbox, GoogleSyncAudit, AddRole, EndRole | **New** — extracted from HumanController |
| **ContactAdminController** | /Contacts | List, Detail, Create | **New** — extracted from HumanController |

#### Community
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **CommunityController** | /Community | Birthdays, Map | **New** — extracted from TeamController |

#### Teams
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **TeamController** | /Teams | Index, Details, Join, Leave, WithdrawRequest, MyTeams | User-facing team interaction |
| **TeamAdminController** | /Teams/{slug} | Members, Resources, Roles, EditPage, request approval/rejection, search | Keep as-is (already well-focused) |
| **TeamManagementController** | /Teams/Admin | Summary, Create, Edit, Delete | **New** — admin team CRUD, extracted from TeamController |
| **TeamSyncController** | /Teams/Sync | Sync, SyncPreview, SyncExecute, SyncExecuteAll | **New** — extracted from TeamController |

#### Governance & Applications
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **GovernanceController** | /Governance | Index (statutes + tier info) | User-facing only |
| **ApplicationController** | /Application | Index, Create, Details, Withdraw | User-facing tier applications |
| **ApplicationAdminController** | /Application/Admin | AdminList, ApplicationDetail | **New** — extracted from ApplicationController |
| **RoleAdminController** | /Governance/Roles | Role assignments list | **New** — extracted from GovernanceController |

#### Onboarding & Review
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **OnboardingReviewController** | /OnboardingReview | Index, Detail, Clear, Flag, Reject | Consent review only |
| **BoardVotingController** | /Board/Voting | Dashboard, Detail, Vote, Finalize | **New** — extracted from OnboardingReviewController |
| **BoardController** | /Board | Index, AuditLog, CheckDriveActivity, GoogleSyncResourceAudit | Keep as-is |

#### Shifts
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **ShiftsController** | /Shifts | Index, SignUp, SignUpRange, Bail, BailRange, Mine, SaveAvailability, RegenerateIcal | User shift browsing |
| **EventSettingsController** | /Shifts/Settings | Settings GET+POST | **New** — extracted from ShiftsController |
| **ShiftAdminController** | /Teams/{slug}/Shifts | All rota/shift/signup admin | Keep as-is |
| **ShiftDashboardController** | /Shifts/Dashboard | Cross-dept dashboard, voluntell | Keep as-is |
| **VolController** | /Vol | Parallel mobile-first UI | Keep as-is (intentional parallel) |

#### Budget & Finance
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **BudgetController** | /Budget | Index, Summary, CategoryDetail, LineItem CRUD | Keep as-is |
| **FinanceController** | /Finance | Year/Group/Category/LineItem admin, AuditLog | Keep as-is |

#### Camps
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **CampController** | /Camps | Directory, Detail, Register, Edit, manage leads/images/names | Keep as-is |
| **CampAdminController** | /Camps/Admin | Approve, Reject, season management, export | Keep as-is |
| **CampApiController** | /api/camps | Public API | Keep as-is |

#### Campaigns & Tickets
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **CampaignController** | /Admin/Campaigns | Campaign CRUD, code management, send waves | Keep as-is |
| **TicketController** | /Tickets | Dashboard, Orders, Attendees, Codes, exports, sync | Keep as-is |

#### Feedback
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **FeedbackController** | /Feedback | List, Detail, Submit, PostMessage, UpdateStatus, GitHubIssue | Keep as-is |
| **FeedbackApiController** | /api/feedback | API endpoints | Keep as-is |

#### Admin Utilities
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **AdminController** | /Admin | Dashboard, Purge, Sync, Logs, Config, EmailPreview, EmailOutbox, Groups, etc. | Keep as-is |
| **AdminEmailController** | /Admin/Email | Workspace account management | Keep as-is |
| **AdminLegalDocumentsController** | /Admin/LegalDocuments | Legal doc admin | Keep as-is |
| **AdminMergeController** | /Admin/MergeRequests | Account merge admin | Keep as-is |

#### API
| Controller | Route Prefix | Actions | Notes |
|-----------|-------------|---------|-------|
| **HumanApiController** | /api/humans | Search | Keep as-is |
| **LogApiController** | /api/logs | Log retrieval | Keep as-is |
| **TimezoneApiController** | /api/timezone | Set timezone | Keep as-is |

---

### Priority Ranking for Splits

If tackling this incrementally, ordered by impact:

1. **HumanController → HumanAdminController** — highest impact, 14 admin actions on a user controller, 850 lines → ~300 + ~550
2. **TeamController → TeamManagementController + TeamSyncController + CommunityController** — 26 actions with mixed concerns, community features hiding on a team controller
3. **OnboardingReviewController → BoardVotingController** — clean conceptual split, two distinct workflows
4. **ProfileController → ProfileEmailController + ProfilePrivacyController** — 20 actions, clear sub-domains
5. **ApplicationController → ApplicationAdminController** — small but clean split
6. **ShiftsController → EventSettingsController** — minor, just 2 actions
7. **GovernanceController → RoleAdminController** — minor, just 1 action
