# Google Integration

## Business Context

Nobodies Collective uses Google Workspace for collaboration. The system integrates with **Shared Drives** and **Google Groups** to manage shared resources for teams. Resources can be either provisioned automatically or linked manually by admins when pre-shared with the service account.

> **Important: All Drive resources are Shared Drives.** This system does not use regular (My Drive) folders. All permission logic accounts for Shared Drive behavior, including inherited permissions from the drive level.

## User Stories

### US-7.1: Team Shared Drive Provisioning
**As a** team
**I want to** have a Shared Drive folder automatically created
**So that** team members can collaborate on documents

**Acceptance Criteria:**
- Folder created in the organization's Shared Drive
- Named appropriately (e.g., "Team: [Team Name]")
- Tracked in system for permission management
- All API calls use `SupportsAllDrives = true`

### US-7.2: Automatic Access Grants
**As a** new team member
**I want to** automatically get access to the team's Google resources
**So that** I can immediately participate in team work

**Acceptance Criteria:**
- Access granted on join approval
- Uses member's Google account (OAuth-linked)
- Appropriate permission level (Editor/Writer)
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
- Only direct permissions are removed (inherited Shared Drive permissions are not touched)

### US-7.4: Resource Sync Status & Drift Detection
**As an** administrator
**I want to** see the status of Google resource sync and any drift
**So that** I can troubleshoot access issues and verify correctness

**Acceptance Criteria:**
- Admin page at `/Admin/GoogleSync` shows all active resources
- Summary cards: Total Resources, In Sync, Drifted, Errors
- Per-resource table showing members to add/remove
- Drifted resources shown first
- "Sync Now" button for manual sync
- Preview is read-only (no changes on page load)
- Inherited Shared Drive permissions are excluded from drift detection

### US-7.5: Link Existing Shared Drive Folder
**As a** Board member or authorized Metalead
**I want to** link an existing Shared Drive folder to a team
**So that** team members automatically get access to the shared folder

**Acceptance Criteria:**
- Admin pastes a Google Drive folder URL
- System validates the service account has access to the folder
- If access denied, shows clear instructions with service account email to share with
- Folder metadata (name, URL) fetched and saved as GoogleResource
- Duplicate links prevented (same folder + team)
- Supports multiple URL formats (direct, u/0, open?id=)
- All API calls use `SupportsAllDrives = true`

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
DriveFolder  = 0  // Shared Drive folder
SharedDrive  = 1  // Shared Drive (reserved)
Group        = 2  // Google Group
```

> **Note:** `DriveFolder` refers to folders within Shared Drives, not regular My Drive folders. The `SharedDrive` value is reserved for future use if top-level Shared Drives need to be tracked separately.

## Service Interface

### IGoogleSyncService
```csharp
public interface IGoogleSyncService
{
    // Shared Drive folder provisioning
    Task<GoogleResource> ProvisionTeamFolderAsync(Guid teamId, string folderName, CancellationToken ct);

    // Permission management
    Task SyncResourcePermissionsAsync(Guid resourceId, CancellationToken ct);
    Task SyncAllResourcesAsync(CancellationToken ct);
    Task<SyncPreviewResult> PreviewSyncAllAsync(CancellationToken ct);

    // Team membership changes
    Task AddUserToTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct);
    Task RemoveUserFromTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct);

    // Status
    Task<GoogleResource?> GetResourceStatusAsync(Guid resourceId, CancellationToken ct);

    // Google Groups
    Task<GoogleResource> ProvisionTeamGroupAsync(Guid teamId, string groupEmail, string groupName, CancellationToken ct);
    Task AddUserToGroupAsync(Guid groupResourceId, string userEmail, CancellationToken ct);
    Task RemoveUserFromGroupAsync(Guid groupResourceId, string userEmail, CancellationToken ct);
    Task SyncTeamGroupMembersAsync(Guid teamId, CancellationToken ct);

    // Restoration
    Task RestoreUserToAllTeamsAsync(Guid userId, CancellationToken ct);
}
```

### SyncPreviewResult / ResourceSyncDiff
```csharp
public class ResourceSyncDiff
{
    public Guid ResourceId { get; init; }
    public string ResourceName, ResourceType, TeamName, GoogleId, Url, ErrorMessage;
    public List<string> MembersToAdd { get; init; } = [];
    public List<string> MembersToRemove { get; init; } = [];
    public bool IsInSync => MembersToAdd.Count == 0 && MembersToRemove.Count == 0 && ErrorMessage == null;
}

public class SyncPreviewResult
{
    public List<ResourceSyncDiff> Diffs { get; init; } = [];
    public int TotalResources => Diffs.Count;
    public int InSyncCount => Diffs.Count(d => d.IsInSync);
    public int DriftCount => Diffs.Count(d => !d.IsInSync);
}
```

## Shared Drive Permission Model

All Drive resources in this system are on Shared Drives. This has important implications for permission management:

### Inherited vs Direct Permissions
- **Inherited permissions** come from the Shared Drive itself (e.g., all drive members get access to all folders). These are NOT managed by this system and are excluded from drift detection and sync.
- **Direct permissions** are set on individual folders within the Shared Drive. These ARE managed by this system.

### Permission Filtering Logic
When listing permissions, the system uses `permissionDetails` from the Drive API to distinguish inherited from direct:
```csharp
// A permission is considered "direct" (managed by us) if:
// 1. Type is "user" (not domain, group, or anyone)
// 2. Role is not "owner"
// 3. Has a valid email address
// 4. Is not a service account (.iam.gserviceaccount.com)
// 5. Is NOT inherited (permissionDetails.All(d => d.Inherited) == false)
```

### API Requirements
All Drive API calls MUST use:
- `SupportsAllDrives = true`
- `Fields` including `permissionDetails` when listing permissions

## Permission Sync

### Full Sync (SyncResourcePermissionsAsync)
For Shared Drive folders:
1. Load expected members from DB (team members where `LeftAt == null`)
2. List current direct permissions from Google (paginated, with `permissionDetails`)
3. Filter to direct managed permissions only (exclude inherited, owner, service account)
4. Add missing permissions (expected but not in Google)
5. Remove stale permissions (in Google but not expected)
6. Update `LastSyncedAt`

For Google Groups:
1. Load expected members from DB
2. List current group members from Google (paginated)
3. Add missing members
4. Remove stale members
5. Update `LastSyncedAt`

### Preview (PreviewSyncAllAsync)
Same diff logic as full sync, but read-only — no writes to Google APIs.

## Resource Linking (Pre-Shared Access Model)

Instead of creating resources with domain-wide delegation, admins can link existing Google resources that have been pre-shared with the service account.

### Access Model
- **Shared Drive folders**: Admin shares the folder with the service account email as "Editor"
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
| `Resources/LinkDrive` | POST | Link a Shared Drive folder by URL |
| `Resources/LinkGroup` | POST | Link a Google Group by email |
| `Resources/{id}/Unlink` | POST | Soft-unlink (IsActive = false) |
| `Resources/{id}/Sync` | POST | Trigger permission sync |

## Admin Sync Page

### Route: `/Admin/GoogleSync`
Global admin page showing drift across all active resources.

| Route | Method | Action |
|-------|--------|--------|
| `GoogleSync` | GET | Preview drift (read-only API calls) |
| `GoogleSync/Apply` | POST | Run full sync now |

### Summary Cards
- **Total Resources** — count of active GoogleResource records
- **In Sync** — resources where expected == actual
- **Drifted** — resources with members to add or remove
- **Errors** — resources where the API call failed

### Resource Table
Per resource: Name, Type (Group/Drive), Team, Status badge, members to add/remove.
Drifted resources shown first, then in-sync.

## Stub Implementations

Both `IGoogleSyncService` and `ITeamResourceService` have stub implementations for development without Google credentials:
- `StubGoogleSyncService`: logs provisioning/sync actions, returns empty preview
- `StubTeamResourceService`: performs real DB operations but simulates Google API validation

Stub vs. real implementation is selected automatically based on whether `GoogleWorkspace:ServiceAccountKeyPath` or `GoogleWorkspace:ServiceAccountKeyJson` is configured.

## Background Jobs

### GoogleResourceReconciliationJob
```
Schedule: 3:00 AM daily (CURRENTLY DISABLED — manual sync only via /Admin/GoogleSync)
Purpose: Full reconciliation of all Google resources with DB state
Process: Calls SyncAllResourcesAsync()
```

> **Currently disabled:** All jobs that modify Google permissions are disabled until the system is validated. Use the manual "Sync Now" button at `/Admin/GoogleSync` instead.

### SystemTeamSyncJob
```
Schedule: Hourly (CURRENTLY DISABLED — modifies Google permissions)
Purpose: Sync system team membership and Google resource permissions
```

### System Team Sync
When system teams are synced, Google permissions are also updated:
- New member → Add to Shared Drive folders + Groups
- Removed member → Remove from Shared Drive folders + Groups

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
| Inherited permission delete | Skip (cannot remove inherited Shared Drive permissions) |

## Security Considerations

1. **Minimal Permissions**: Only grant Writer, not Owner
2. **Service Account**: Never expose service account credentials
3. **Audit Trail**: Log all permission changes
4. **Revocation**: Ensure timely removal on leave/suspension
5. **Domain Restriction**: Only add users within the organization domain
6. **Inherited Permissions**: Never attempt to remove inherited Shared Drive permissions

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
    "UseSharedDrives": true,
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
