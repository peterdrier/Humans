# Google Integration

## Business Context

Nobodies Collective uses Google Workspace for collaboration. The system integrates with Google Drive and Google Groups to manage shared resources for teams. Resources can be either provisioned automatically or linked manually by admins when pre-shared with the service account.

## User Stories

### US-7.1: Team Folder Provisioning
**As a** team
**I want to** have a shared Google Drive folder automatically created
**So that** team members can collaborate on documents

**Acceptance Criteria:**
- Folder created when team is created
- Named appropriately (e.g., "Team: [Team Name]")
- Located in organization's shared drives
- Tracked in system for permission management

### US-7.2: Automatic Access Grants
**As a** new team member
**I want to** automatically get access to the team's Google resources
**So that** I can immediately participate in team work

**Acceptance Criteria:**
- Access granted on join approval
- Uses member's Google account (OAuth-linked)
- Appropriate permission level (Editor)
- Notification that access was granted

### US-7.3: Automatic Access Revocation
**As a** team
**I want** former members to lose access to resources
**So that** team documents remain protected

**Acceptance Criteria:**
- Access revoked on leave
- Access revoked on removal by admin
- Revocation logged for audit
- Works even if user account is disabled

### US-7.4: Resource Sync Status
**As an** administrator
**I want to** see the status of Google resource sync
**So that** I can troubleshoot access issues

**Acceptance Criteria:**
- Shows last sync timestamp
- Indicates any errors
- Lists provisioned resources
- Manual resync option

### US-7.5: Link Existing Drive Folder
**As a** Board member or authorized Metalead
**I want to** link an existing Google Drive folder to a team
**So that** team members automatically get access to the shared folder

**Acceptance Criteria:**
- Admin pastes a Google Drive folder URL
- System validates the service account has access to the folder
- If access denied, shows clear instructions with service account email to share with
- Folder metadata (name, URL) fetched and saved as GoogleResource
- Duplicate links prevented (same folder + team)
- Supports multiple URL formats (direct, u/0, open?id=)

### US-7.6: Link Existing Google Group
**As a** Board member or authorized Metalead
**I want to** link an existing Google Group to a team
**So that** team membership automatically syncs with group membership

**Acceptance Criteria:**
- Admin enters a Google Group email address
- System validates the service account has access to the group
- If access denied, shows clear instructions with service account email
- Group metadata (name, ID) fetched and saved as GoogleResource
- Duplicate links prevented (same group + team)

### US-7.7: Unlink Resource
**As a** Board member or authorized Metalead
**I want to** unlink a Google resource from a team
**So that** the association is removed without deleting the resource

**Acceptance Criteria:**
- Soft unlink: sets IsActive = false (preserves audit trail)
- Resource disappears from active list
- Google permissions are NOT automatically revoked (manual cleanup)

### US-7.8: Metalead Resource Management
**As an** admin
**I want to** control whether Metaleads can manage team resources
**So that** I can delegate resource management when appropriate

**Acceptance Criteria:**
- Controlled by `TeamResourceManagement:AllowMetaleadsToManageResources` config setting
- Default: false (only Board members can manage)
- When enabled, Metaleads can link/unlink/sync resources for their teams

## Data Model

### GoogleResource Entity
```
GoogleResource
├── Id: Guid
├── ResourceType: GoogleResourceType [enum]
├── GoogleId: string (256) [unique, Google's ID]
├── Name: string (512)
├── Url: string? (2048) [Google Drive URL]
├── TeamId: Guid? (FK → Team, optional)
├── UserId: Guid? (FK → User, optional)
├── ProvisionedAt: Instant
├── LastSyncedAt: Instant?
├── IsActive: bool
└── ErrorMessage: string? (2000)
```

### GoogleResourceType Enum
```
DriveFolder  = 0  // Google Drive folder
SharedDrive  = 1  // Shared Drive (future)
Group        = 2  // Google Group
```

## Service Interface

### IGoogleSyncService
```csharp
public interface IGoogleSyncService
{
    // Team folder provisioning
    Task<GoogleResource> ProvisionTeamFolderAsync(
        Guid teamId,
        string folderName,
        CancellationToken ct);

    // User folder provisioning (future)
    Task<GoogleResource> ProvisionUserFolderAsync(
        Guid userId,
        string folderName,
        CancellationToken ct);

    // Permission management
    Task SyncResourcePermissionsAsync(Guid resourceId, CancellationToken ct);
    Task SyncAllResourcesAsync(CancellationToken ct);

    // Team membership changes
    Task AddUserToTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct);
    Task RemoveUserFromTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct);

    // Status
    Task<GoogleResource?> GetResourceStatusAsync(Guid resourceId, CancellationToken ct);
}
```

## Provisioning Flow

### Team Folder Creation
```
┌──────────────────┐
│ Team Created     │
└────────┬─────────┘
         │
         ▼
┌──────────────────────┐
│ ProvisionTeamFolder  │
│ - Name: "Team: X"    │
│ - Parent: Org Drive  │
└────────┬─────────────┘
         │
         ▼
┌──────────────────────┐
│ Google Drive API     │
│ files.create()       │
└────────┬─────────────┘
         │
         ▼
┌──────────────────────┐
│ Store GoogleResource │
│ - GoogleId           │
│ - URL                │
│ - ProvisionedAt      │
└──────────────────────┘
```

### Permission Sync
```
┌────────────────────┐
│ User Joins Team    │
└────────┬───────────┘
         │
         ▼
┌────────────────────────┐
│ AddUserToTeamResources │
└────────┬───────────────┘
         │
         ▼
┌────────────────────────┐
│ For each team resource:│
│ - Get user's email     │
│ - Google permissions   │
│   API call             │
│ - Grant Editor role    │
└────────┬───────────────┘
         │
         ▼
┌────────────────────────┐
│ Log success/error      │
└────────────────────────┘
```

## Resource Linking (Pre-Shared Access Model)

Instead of creating resources with domain-wide delegation, admins can link existing Google resources that have been pre-shared with the service account.

### Access Model
- **Drive folders**: Admin shares the folder with the service account email as "Editor"
- **Google Groups**: Admin adds the service account as a Group Manager
- The service account authenticates as itself (no impersonation) for validation/linking

### ITeamResourceService
Separate interface from IGoogleSyncService for linking/validation (not provisioning):
```csharp
public interface ITeamResourceService
{
    Task<IReadOnlyList<GoogleResource>> GetTeamResourcesAsync(Guid teamId, ...);
    Task<LinkResourceResult> LinkDriveFolderAsync(Guid teamId, string folderUrl, ...);
    Task<LinkResourceResult> LinkGroupAsync(Guid teamId, string groupEmail, ...);
    Task UnlinkResourceAsync(Guid resourceId, ...);
    Task<bool> CanManageTeamResourcesAsync(Guid teamId, Guid userId, ...);
    Task<string> GetServiceAccountEmailAsync(...);
}
```

### Resource Linking Flow
```
┌────────────────────────┐
│ Admin pastes URL/email │
└────────┬───────────────┘
         │
         ▼
┌────────────────────────┐
│ Parse & validate input │
│ (URL → folder ID)      │
└────────┬───────────────┘
         │
         ▼
┌──────────────────────────┐
│ Google API (as service   │
│ account, no impersonation│
│ - Files.Get / Groups.Get │
└────────┬─────────────────┘
         │
    ┌────┴─────┐
    │          │
 Success    Failure
    │          │
    ▼          ▼
┌────────┐ ┌─────────────────────┐
│ Save   │ │ Show error +        │
│ record │ │ service account     │
│        │ │ email for sharing   │
└────────┘ └─────────────────────┘
```

### Drive Folder URL Parsing
Supports multiple Google Drive URL formats:
- `https://drive.google.com/drive/folders/{id}`
- `https://drive.google.com/drive/u/0/folders/{id}`
- `https://drive.google.com/open?id={id}`
- `https://drive.google.com/drive/folders/{id}?usp=sharing`
- Direct folder ID

### Authorization
- Board members: can manage resources for any team
- Metaleads: controlled by `TeamResourceManagement:AllowMetaleadsToManageResources` (default: false)

### Route: `/Teams/{slug}/Admin/Resources`
Actions:
| Route | Method | Action |
|-------|--------|--------|
| `Resources` | GET | View linked resources + link forms |
| `Resources/LinkDrive` | POST | Link a Drive folder by URL |
| `Resources/LinkGroup` | POST | Link a Google Group by email |
| `Resources/{id}/Unlink` | POST | Soft-unlink (IsActive = false) |
| `Resources/{id}/Sync` | POST | Trigger permission sync |

## Stub Implementations

Both `IGoogleSyncService` and `ITeamResourceService` have stub implementations for development without Google credentials:
- `StubGoogleSyncService`: logs provisioning/sync actions
- `StubTeamResourceService`: performs real DB operations but simulates Google API validation

Stub vs. real implementation is selected automatically based on whether `GoogleWorkspace:ServiceAccountKeyPath` or `GoogleWorkspace:ServiceAccountKeyJson` is configured.

## Error Handling

### Retry Strategy
```
On Google API error:
  1. Log error with details
  2. Store error in GoogleResource.ErrorMessage
  3. Set IsActive = false if persistent
  4. Background job will retry on next sync
```

### Error Scenarios
| Error | Handling |
|-------|----------|
| Rate limit exceeded | Exponential backoff |
| User not in domain | Skip, log warning |
| Folder not found | Re-provision |
| Permission denied | Alert admin |

## Background Jobs

### GoogleResourceProvisionJob
```
Runs: On demand or scheduled

Tasks:
  1. Find teams without GoogleResource
  2. Provision folders for each
  3. Sync permissions for all resources
  4. Report errors
```

### System Team Sync
When system teams are synced, Google permissions are also updated:
- New member → Add to resources
- Removed member → Remove from resources

## Security Considerations

1. **Minimal Permissions**: Only grant Editor, not Owner
2. **Service Account**: Never expose service account credentials
3. **Audit Trail**: Log all permission changes
4. **Revocation**: Ensure timely removal on leave/suspension
5. **Domain Restriction**: Only add users within the organization domain

## Configuration

### GoogleWorkspace Settings
```json
{
  "GoogleWorkspace": {
    "ServiceAccountKeyPath": "/secrets/google-sa.json",
    "ServiceAccountKeyJson": "",
    "ImpersonateUser": "admin@nobodies.team",
    "Domain": "nobodies.team",
    "TeamFoldersParentId": "",
    "UseSharedDrives": false,
    "Groups": {
      "WhoCanViewMembership": "ALL_MEMBERS_CAN_VIEW",
      "WhoCanPostMessage": "ANYONE_CAN_POST",
      "AllowExternalMembers": true
    }
  }
}
```

### TeamResourceManagement Settings
```json
{
  "TeamResourceManagement": {
    "AllowMetaleadsToManageResources": false
  }
}
```

## Monitoring

### Metrics
- Resources provisioned (counter)
- Permission grants/revocations (counter)
- API errors by type (counter)
- Sync duration (histogram)

### Alerts
- Repeated API failures
- Permission sync backlog
- Resources in error state

## Related Features

- [Teams](06-teams.md) - Triggers resource provisioning
- [Background Jobs](08-background-jobs.md) - Resource sync job
- [Authentication](01-authentication.md) - User Google identity
