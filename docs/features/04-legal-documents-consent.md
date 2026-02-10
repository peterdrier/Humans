# Legal Documents & Consent Management

## Business Context

As a nonprofit operating in Spain and the EU, Nobodies Collective must comply with GDPR requirements for explicit consent. The system manages multiple legal documents with version tracking and maintains an immutable audit trail of all consent actions. Documents are team-scoped, multi-language, and configurable through an admin GUI.

## Key Concepts

- **Team-scoped documents**: Every document belongs to a Team. Documents on the Volunteers team (all active members) are effectively global. Other teams (Board, Metaleads, user-created) can have team-specific required documents.
- **Per-document grace period**: Each document has its own `GracePeriodDays` (default 7). After grace expires without re-consent, the user is removed from that team.
- **Multi-language content**: Document versions store content in a `Content` dictionary keyed by language code ("es", "en", "de", etc.). Spanish ("es") is always canonical/legally binding.
- **Folder-based GitHub sync**: Admin sets a `GitHubFolderPath`; sync discovers translations by naming convention: `name.md` (Spanish), `name-en.md` (English), `name-de.md` (German), etc.
- **Admin CRUD**: Documents are created and managed via the admin UI at `/Admin/LegalDocuments`, not in config files.

## User Stories

### US-4.1: View Required Documents (Team-Grouped)
**As a** member
**I want to** see which legal documents I need to sign, grouped by team
**So that** I can maintain my active membership in each team

**Acceptance Criteria:**
- Documents are grouped by team (card per team)
- Each team group shows "All signed" or "Action required" badge
- Shows consent status, version, effective date, and last updated for each document
- Teams with pending documents are shown first
- Volunteers team docs always shown (global requirement)

### US-4.2: Review Document Content (Multi-Language)
**As a** member
**I want to** read a legal document in my preferred language before consenting
**So that** I understand what I'm agreeing to

**Acceptance Criteria:**
- Dynamic language tabs based on available translations in Content dictionary
- Tab ordering: Castellano (Spanish) always first, then alphabetical by native language name
- Active tab defaults to user's browser language
- Spanish tab marked with "(Legal)" suffix as canonical version
- Non-canonical language tabs show translation disclaimer
- Version number and effective date displayed
- Consent checkbox requires explicit action

### US-4.3: Provide Explicit Consent
**As a** member
**I want to** explicitly consent to a legal document
**So that** my agreement is legally recorded

**Acceptance Criteria:**
- Must explicitly check consent checkbox
- Consent is timestamped
- IP address and user agent recorded
- Content hash captured from canonical Spanish content
- Cannot modify or delete consent record

### US-4.4: Admin Document Management
**As a** system administrator
**I want to** create, edit, and manage legal documents through a GUI
**So that** I don't need code changes to add or modify documents

**Acceptance Criteria:**
- Create documents with name, team, GitHub folder path, grace period, required/active flags
- Edit existing documents
- Archive documents (soft-delete via IsActive=false)
- Trigger manual sync for individual documents
- View version count and last sync timestamp
- Filter document list by team

### US-4.5: Sync Documents from GitHub
**As a** system administrator
**I want** legal documents to sync from a GitHub repository
**So that** the legal team can manage documents via version control

**Acceptance Criteria:**
- Documents auto-sync on schedule (daily)
- Folder-based discovery: admin sets folder, system discovers language files by convention
- New versions detected via commit SHA comparison
- All discovered languages stored in Content dictionary
- Admin can trigger manual sync per document

### US-4.6: Re-consent on Document Updates
**As a** member
**I want to** be notified when a document I signed is updated
**So that** I can review changes and re-consent

**Acceptance Criteria:**
- Email notification when re-consent required
- Only team members notified (not all users)
- Old consent remains in audit trail
- Per-document grace period before team removal

## Data Model

### LegalDocument Entity
```
LegalDocument
+-- Id: Guid
+-- Name: string (256)
+-- TeamId: Guid (FK -> Team) [required]
+-- Team: Team [navigation]
+-- GracePeriodDays: int (default 7)
+-- GitHubFolderPath: string? (512)
+-- CurrentCommitSha: string (40)
+-- IsRequired: bool
+-- IsActive: bool
+-- CreatedAt: Instant
+-- LastSyncedAt: Instant
+-- Navigation: Versions, Team
```

### DocumentVersion Entity
```
DocumentVersion
+-- Id: Guid
+-- LegalDocumentId: Guid (FK)
+-- VersionNumber: string (50)
+-- CommitSha: string (40)
+-- Content: Dictionary<string, string> [jsonb, keyed by lang code]
+-- EffectiveFrom: Instant
+-- RequiresReConsent: bool
+-- CreatedAt: Instant
+-- ChangesSummary: string? (2000)
```

### ConsentRecord Entity (IMMUTABLE)
```
ConsentRecord
+-- Id: Guid
+-- UserId: Guid (FK -> User)
+-- DocumentVersionId: Guid (FK -> DocumentVersion)
+-- ConsentedAt: Instant
+-- IpAddress: string (45)
+-- UserAgent: string (1024)
+-- ContentHash: string (64) [SHA-256 of canonical Spanish content]
+-- ExplicitConsent: bool [always true for valid records]

Database triggers prevent UPDATE and DELETE operations.
```

### SystemTeamIds Constants
```
Volunteers:  00000000-0000-0000-0001-000000000001  (global docs)
Metaleads:   00000000-0000-0000-0001-000000000002
Board:       00000000-0000-0000-0001-000000000003
```

## Consent Flow

```
View Consents (grouped by team)
    |
Select Document to Review
    |
Display Full Document (dynamic language tabs)
    |
User Reads Document (switches languages as needed)
    |
Check Explicit Consent Box
    |
Create ConsentRecord
  - Timestamp (Instant)
  - IP Address
  - User Agent
  - Content Hash (SHA-256 of Spanish/canonical)
  - ExplicitConsent = true
    |
Trigger SyncVolunteersMembershipForUserAsync
  - Checks if user now has all required consents + is approved
  - If eligible, immediately adds to Volunteers team
  - Grants ActiveMember claim → full app access
```

## Document Sync Process

```
Scheduled Job (Daily)
    |
Iterate active documents from database
    |
For each document with GitHubFolderPath:
    |
    +-- List folder contents via GitHub API
    +-- Match files by convention: name.md (es), name-en.md (en), etc.
    +-- Fetch canonical file, compare commit SHA
    |
    [Same SHA] -> Skip, update LastSyncedAt
    [Different SHA] -> Fetch all language files
    |
    Create new DocumentVersion with Content dict
    |
    Notify affected team members (not all users)
    |
    Per-document grace period starts
```

## Admin Routes

| Route | Method | Action |
|-------|--------|--------|
| `/Admin/LegalDocuments` | GET | List all documents (optional team filter) |
| `/Admin/CreateLegalDocument` | GET | Create form |
| `/Admin/CreateLegalDocument` | POST | Create handler |
| `/Admin/EditLegalDocument/{id}` | GET | Edit form |
| `/Admin/EditLegalDocument/{id}` | POST | Update handler |
| `/Admin/ArchiveLegalDocument/{id}` | POST | Soft-delete (IsActive=false) |
| `/Admin/SyncLegalDocument/{id}` | POST | Trigger single-document sync |

## GDPR Compliance

### Immutability Guarantee
The `consent_records` table has PostgreSQL triggers that:
- **Prevent DELETE**: `RAISE EXCEPTION 'Consent records cannot be deleted'`
- **Prevent UPDATE**: `RAISE EXCEPTION 'Consent records cannot be modified'`

Only INSERT is allowed, ensuring complete audit trail integrity.

### Captured Metadata
| Field | Purpose | GDPR Requirement |
|-------|---------|------------------|
| ConsentedAt | When consent given | Timestamp proof |
| IpAddress | Network location | Geographic evidence |
| UserAgent | Browser/device info | Device identification |
| ContentHash | Document integrity | Proof of exact content |
| ExplicitConsent | Affirmative action | Active consent proof |

### Right to Access
Members can view all their consent records through the Consent index page, satisfying GDPR Article 15 (Right of Access).

## Version Management

### Required Consent Logic
```
For each team the user belongs to:
  For each required, active document on that team:
    1. Get latest version where EffectiveFrom <= now
    2. Check if user has consented to that version
    3. If not, document is "requiring consent"
    4. If RequiresReConsent on new version, old consent invalid
```

### Re-consent Trigger
When a new version is published with `RequiresReConsent = true`:
1. Team members with existing consent are notified
2. Their consent status for that team becomes incomplete
3. They must review and consent to new version
4. Original consent remains in audit trail
5. Per-document grace period applies before team removal

## Business Rules

1. **Explicit Consent Only**: Checkbox must be actively checked
2. **Full Document View**: Cannot consent without viewing document
3. **Version Binding**: Consent is to specific version, not document
4. **No Retroactive Changes**: Consent records are immutable
5. **Per-Document Grace Period**: Each document has configurable GracePeriodDays (default 7)
6. **Multi-Language**: Documents available in any number of languages; Spanish is canonical
7. **Team-Scoped**: Documents belong to teams; Volunteers team = global
8. **Admin-Managed**: Documents created/edited via GUI, not config files

## Related Features

- [Volunteer Status](05-volunteer-status.md) - Status depends on consent completion
- [Background Jobs](08-background-jobs.md) - Document sync and reminder jobs
- [Profiles](02-profiles.md) - Consent status shown on profile
