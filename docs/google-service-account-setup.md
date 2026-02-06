# Google Service Account Setup

## Service Account Permissions

The application uses a Google service account to manage Drive folders and Google Groups for teams. The service account authenticates as itself (no domain-wide delegation or impersonation).

### Required Google Cloud APIs

Enable these in **Google Cloud Console** > **APIs & Services**:

| API | Purpose |
|-----|---------|
| **Google Drive API** | Read folder metadata, create folders, manage sharing permissions |
| **Admin SDK API** | Read group info, manage group membership |

### Required OAuth Scopes

| Scope | Used By | Purpose |
|-------|---------|---------|
| `https://www.googleapis.com/auth/drive` | GoogleWorkspaceSyncService | Create folders, manage permissions |
| `https://www.googleapis.com/auth/drive.readonly` | TeamResourceService | Validate folder access when linking |
| `https://www.googleapis.com/auth/admin.directory.group` | GoogleWorkspaceSyncService | Create groups, manage members |
| `https://www.googleapis.com/auth/admin.directory.group.member` | GoogleWorkspaceSyncService | Add/remove group members |
| `https://www.googleapis.com/auth/admin.directory.group.readonly` | TeamResourceService, Health Check | Validate group access, health checks |

### Pre-Shared Resource Access

Since the service account does NOT use impersonation, it can only access resources that have been explicitly shared with it.

#### Drive Folders
- Share the folder with the service account email as **Contributor** (or Content Manager)
- The service account can then:
  - Read folder metadata (`Files.Get`)
  - Create subfolders (`Files.Create`)
  - Manage sharing permissions (`Permissions.Create`, `Permissions.List`, `Permissions.Delete`)

#### Google Groups
- Add the service account email as a **Manager** of the group
- The service account can then:
  - Read group info (`Groups.Get`, `Groups.List`)
  - Add members (`Members.Insert`)
  - Remove members (`Members.Delete`)
  - List members (`Members.List`)

### API Operations by Feature

#### Resource Linking (TeamResourceService)
| Operation | API Call | Required Access |
|-----------|----------|-----------------|
| Link Drive folder | `Files.Get` | Folder shared with service account |
| Link Google Group | `Groups.Get` | Service account is group Manager |

#### Permission Sync (GoogleWorkspaceSyncService)
| Operation | API Call | Required Access |
|-----------|----------|-----------------|
| Create team folder | `Files.Create` | Parent folder shared with service account |
| Grant folder access | `Permissions.Create` | Folder shared with service account |
| Revoke folder access | `Permissions.List` + `Permissions.Delete` | Folder shared with service account |
| Create Google Group | `Groups.Insert` | Admin SDK group scope |
| Add user to group | `Members.Insert` | Service account is group Manager |
| Remove user from group | `Members.Delete` | Service account is group Manager |
| Sync group members | `Members.List` | Service account is group Manager |

#### Health Check
| Operation | API Call | Required Access |
|-----------|----------|-----------------|
| Verify connectivity | `Groups.List` (max 1 result) | Admin SDK group readonly scope |

## Setup Steps

1. **Create a service account** in Google Cloud Console > IAM & Admin > Service Accounts
2. **Create a JSON key** for the service account (Keys tab > Add Key > JSON)
3. **Enable APIs**: Google Drive API and Admin SDK API
4. **Configure the key** in the application:
   - User secrets: `dotnet user-secrets set "GoogleWorkspace:ServiceAccountKeyPath" "/path/to/key.json"`
   - Or set `GoogleWorkspace:ServiceAccountKeyJson` with the JSON contents
5. **Assign Groups Admin role** in Google Workspace Admin Console (admin.google.com):
   - Go to **Account** > **Admin roles** > **Groups Admin**
   - Click **Assign service accounts**
   - Enter the service account email
   - This grants Admin SDK access for group management without impersonation
6. **Share resources** with the service account email:
   - Drive folders: share as Contributor
   - Google Groups: add as Manager

## Configuration

```json
{
  "GoogleWorkspace": {
    "ServiceAccountKeyPath": "/path/to/service-account-key.json",
    "Domain": "nobodies.team"
  },
  "TeamResourceManagement": {
    "AllowMetaleadsToManageResources": false
  }
}
```

## Important: No Impersonation

This application does **not** use domain-wide delegation or user impersonation. The service account authenticates as itself. This means:
- No need to configure domain-wide delegation in Google Workspace Admin Console
- The service account can only access resources explicitly shared with it
- Admins must pre-share resources before linking them in the application
