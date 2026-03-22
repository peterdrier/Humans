# Google & External Service Setup

This guide covers creating all credentials the application needs. Production and QA/dev should use **separate Google Cloud projects** so credential rotation and quota limits don't affect each other.

## Overview

| Credential | Config Key | Type | Purpose |
|------------|-----------|------|---------|
| Google OAuth Client | `Authentication:Google:ClientId/ClientSecret` | OAuth 2.0 Client ID | "Sign in with Google" for users |
| Google Workspace Service Account | `GoogleWorkspace:ServiceAccountKeyPath` or `ServiceAccountKeyJson` | Service Account Key | Drive, Groups, Drive Activity APIs |
| Google Maps API Key | `GoogleMaps:ApiKey` | API Key | Volunteer location map, address autocomplete |
| GitHub Access Token | `GitHub:AccessToken` | Personal Access Token | Legal document sync from GitHub repo |

---

## Step 1: Create Google Cloud Projects

Create **two** projects in [Google Cloud Console](https://console.cloud.google.com/):

| Project | Suggested Name | Purpose |
|---------|---------------|---------|
| Production | `humans-prod` | Live site (`humans.nobodies.team`) |
| QA/Dev | `humans-dev` | Local dev + staging (`humans.n.burn.camp`) |

All Google credentials below are created per-project, giving each environment its own keys, quotas, and audit trail.

---

## Step 2: Google OAuth Client (User Login)

Users sign in with "Sign in with Google". This requires an OAuth 2.0 Client ID in each project.

### 2a. Configure the OAuth Consent Screen

1. Go to **APIs & Services** > **OAuth consent screen**
2. Choose **External** user type (allows any Google account, not just your domain)
3. Fill in:
   - **App name**: `Humans` (or `Humans Dev` for the dev project)
   - **User support email**: `admin@nobodies.team`
   - **Authorized domains**: `nobodies.team`, `burn.camp`
   - **Developer contact email**: `admin@nobodies.team`
4. **Scopes**: Add `email` and `profile` (these are non-sensitive, no verification needed)
5. **Test users**: Not needed since the app doesn't request sensitive scopes
6. Click **Publish App** to move out of testing mode (otherwise only test users can log in)

### 2b. Create the OAuth Client ID

1. Go to **APIs & Services** > **Credentials** > **Create Credentials** > **OAuth client ID**
2. Application type: **Web application**
3. Name: `Humans Web` (or `Humans Web Dev`)
4. **Authorized JavaScript origins**:
   - Production: `https://humans.nobodies.team`
   - QA/Dev: `https://humans.n.burn.camp`, `https://localhost:5001`, `http://localhost:5000`
5. **Authorized redirect URIs**:
   - Production: `https://humans.nobodies.team/signin-google`
   - QA/Dev: `https://humans.n.burn.camp/signin-google`, `https://localhost:5001/signin-google`
6. Copy the **Client ID** and **Client Secret**

### 2c. Configure in the App

Local development (user secrets):
```bash
dotnet user-secrets set "Authentication:Google:ClientId" "your-client-id.apps.googleusercontent.com"
dotnet user-secrets set "Authentication:Google:ClientSecret" "GOCSPX-your-secret"
```

Docker / production (environment variables):
```
Authentication__Google__ClientId=your-client-id.apps.googleusercontent.com
Authentication__Google__ClientSecret=GOCSPX-your-secret
```

---

## Step 3: Google Workspace Service Account

The service account manages Drive folders, Google Groups, and monitors Drive Activity. It authenticates as itself (no domain-wide delegation or impersonation).

### 3a. Enable APIs

Go to **APIs & Services** > **Library** and enable:

| API | Purpose |
|-----|---------|
| **Google Drive API** | Create folders, manage sharing permissions |
| **Admin SDK API** | Manage Google Groups, group membership, and list user accounts |
| **Google Drive Activity API** | Monitor permission changes on shared resources |

### 3b. Create the Service Account

1. Go to **IAM & Admin** > **Service Accounts** > **Create Service Account**
2. Name: `humans-sync` (or `humans-sync-dev`)
3. Skip the optional "Grant access" steps
4. Click **Done**
5. Open the new service account > **Keys** tab > **Add Key** > **Create new key** > **JSON**
6. Save the downloaded JSON file securely — this is the only copy

The service account email will look like: `humans-sync@humans-prod.iam.gserviceaccount.com`

### 3c. Assign Admin Roles

The service account needs admin roles to manage Google Groups and list user accounts:

1. Go to [Google Workspace Admin Console](https://admin.google.com/) (not Cloud Console)
2. **Account** > **Admin roles**

**Groups Admin** (required for group management):
3. Select **Groups Admin** > **Assign service accounts**
4. Enter the service account email from step 3b

**User Management Admin** (required for listing @nobodies.team accounts):
5. Select **User Management Admin** > **Assign service accounts**
6. Enter the same service account email

Both roles grant Admin SDK access without domain-wide delegation or impersonation.

### 3d. Share Resources with the Service Account

Since the service account doesn't use impersonation, it can only access resources explicitly shared with it:

- **Drive folders (Shared Drives)**: Share with the service account email as **Contributor** (or Content Manager)
- **Google Groups**: Add the service account email as a **Manager** of each group

### 3e. Configure in the App

Local development (user secrets):
```bash
dotnet user-secrets set "GoogleWorkspace:ServiceAccountKeyPath" "/path/to/service-account-key.json"
```

Docker / production (environment variable with JSON content):
```
GoogleWorkspace__ServiceAccountKeyJson={"type":"service_account","project_id":"...","private_key":"..."}
```

Use `ServiceAccountKeyPath` for file-based credentials, or `ServiceAccountKeyJson` for inline JSON (useful in container environments where mounting files is inconvenient).

### Service Account OAuth Scopes (Automatic)

The app requests these scopes automatically — no manual configuration needed:

| Scope | Used By | Purpose |
|-------|---------|---------|
| `https://www.googleapis.com/auth/drive` | GoogleWorkspaceSyncService | Create folders, manage permissions |
| `https://www.googleapis.com/auth/drive.readonly` | TeamResourceService | Validate folder access when linking |
| `https://www.googleapis.com/auth/admin.directory.group` | GoogleWorkspaceSyncService | Create groups, manage members |
| `https://www.googleapis.com/auth/admin.directory.group.member` | GoogleWorkspaceSyncService | Add/remove group members |
| `https://www.googleapis.com/auth/admin.directory.group.readonly` | TeamResourceService, Health Check | Validate group access |
| `https://www.googleapis.com/auth/admin.directory.user.readonly` | GoogleWorkspaceUserService | List @nobodies.team user accounts |
| `https://www.googleapis.com/auth/drive.activity.readonly` | DriveActivityMonitorService | Monitor permission changes |

### API Operations Reference

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

#### User Account Listing (GoogleWorkspaceUserService)
| Operation | API Call | Required Access |
|-----------|----------|-----------------|
| List domain accounts | `Users.List` | User Management Admin role |

#### Drive Activity Monitoring
| Operation | API Call | Required Access |
|-----------|----------|-----------------|
| Check permission changes | `Activity.Query` | Drive Activity API enabled, folders shared with service account |

#### Health Check
| Operation | API Call | Required Access |
|-----------|----------|-----------------|
| Verify connectivity | `Groups.List` (max 1 result) | Admin SDK group readonly scope |

---

## Step 4: Google Maps API Key

Used for the volunteer location map and address autocomplete on the profile edit page.

### 4a. Create the API Key

1. Go to **APIs & Services** > **Library** and enable **Maps JavaScript API** and **Places API**
2. Go to **Credentials** > **Create Credentials** > **API key**
3. Click **Edit** on the new key to add restrictions:

### 4b. Restrict the Key

**Application restriction** — HTTP referrers:
- Production: `humans.nobodies.team/*`
- QA/Dev: `humans.n.burn.camp/*`, `localhost:*`

**API restriction** — restrict to:
- Maps JavaScript API
- Places API

### 4c. Configure in the App

Local development (user secrets):
```bash
dotnet user-secrets set "GoogleMaps:ApiKey" "AIza..."
```

Docker / production (environment variable):
```
GoogleMaps__ApiKey=AIza...
```

---

## Step 5: GitHub Access Token

Used to sync legal document content from the `nobodies-collective/legal` GitHub repository.

### 5a. Create a Fine-Grained Personal Access Token

1. Go to [GitHub Settings > Developer settings > Personal access tokens > Fine-grained tokens](https://github.com/settings/tokens?type=beta)
2. **Token name**: `humans-legal-sync` (or `humans-legal-sync-dev`)
3. **Repository access**: Select **Only select repositories** > `nobodies-collective/legal`
4. **Permissions**: Repository permissions > **Contents**: Read-only
5. **Expiration**: Set a reminder to rotate before expiry

### 5b. Configure in the App

Local development (user secrets):
```bash
dotnet user-secrets set "GitHub:AccessToken" "github_pat_..."
```

Docker / production (environment variable):
```
GitHub__AccessToken=github_pat_...
```

---

## Full Configuration Reference

### appsettings.json (checked into source — no secrets)

```json
{
  "Authentication": {
    "Google": {
      "ClientId": "",
      "ClientSecret": ""
    }
  },
  "GoogleWorkspace": {
    "ServiceAccountKeyPath": "",
    "ServiceAccountKeyJson": "",
    "Domain": "nobodies.team",
    "TeamFoldersParentId": "",
    "UseSharedDrives": false,
    "Groups": {
      "WhoCanViewMembership": "ALL_MEMBERS_CAN_VIEW",
      "WhoCanPostMessage": "ANYONE_CAN_POST",
      "AllowExternalMembers": true
    }
  },
  "GoogleMaps": {
    "ApiKey": ""
  },
  "GitHub": {
    "Owner": "nobodies-collective",
    "Repository": "legal",
    "Branch": "main",
    "AccessToken": ""
  }
}
```

### Environment Variables (docker-compose / Coolify)

```bash
# Google OAuth (user login)
Authentication__Google__ClientId=...
Authentication__Google__ClientSecret=...

# Google Workspace (service account — use one or the other)
GoogleWorkspace__ServiceAccountKeyPath=/path/to/key.json
GoogleWorkspace__ServiceAccountKeyJson={"type":"service_account",...}

# Google Maps
GoogleMaps__ApiKey=AIza...

# GitHub
GitHub__AccessToken=github_pat_...

# Database
POSTGRES_PASSWORD=...
```

### .env.example

The `.env.example` at the repo root has the Docker Compose variables. Copy to `.env` and fill in.

---

## Dev vs Production Checklist

| Item | Dev/QA | Production |
|------|--------|------------|
| Google Cloud project | `humans-dev` | `humans-prod` |
| OAuth redirect URIs | `localhost`, `humans.n.burn.camp` | `humans.nobodies.team` |
| OAuth consent screen | Can stay in "Testing" mode | Must be **Published** |
| Maps API key referrer restriction | `localhost:*`, `humans.n.burn.camp/*` | `humans.nobodies.team/*` |
| Service account | `humans-sync@humans-dev.iam.gserviceaccount.com` | `humans-sync@humans-prod.iam.gserviceaccount.com` |
| Service account Groups Admin role | Assign in Workspace Admin | Assign in Workspace Admin |
| Service account User Management Admin role | Assign in Workspace Admin | Assign in Workspace Admin |
| Shared Drive folders | Share with dev service account | Share with prod service account |
| Google Groups | Add dev service account as Manager | Add prod service account as Manager |
| GitHub token | Can share with dev | Separate token recommended |

---

## Verifying the Setup

After configuring credentials, check the **Admin > Configuration** page (`/Admin/Configuration`). It shows which keys are set and which are missing. The health check endpoint (`/health/ready`) also verifies Google Workspace connectivity.
