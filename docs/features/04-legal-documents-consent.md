# Legal Documents & Consent Management

## Business Context

As a nonprofit operating in Spain and the EU, Nobodies Collective must comply with GDPR requirements for explicit consent. The system manages multiple legal documents (privacy policy, terms, code of conduct) with version tracking and maintains an immutable audit trail of all consent actions.

## User Stories

### US-4.1: View Required Documents
**As a** member
**I want to** see which legal documents I need to sign
**So that** I can maintain my active membership status

**Acceptance Criteria:**
- Lists all required documents
- Shows consent status for each (signed/not signed)
- Displays current version for each document
- Highlights documents needing attention

### US-4.2: Review Document Content
**As a** member
**I want to** read a legal document before consenting
**So that** I understand what I'm agreeing to

**Acceptance Criteria:**
- View document in preferred language (Spanish/English)
- See version number and effective date
- Document is displayed in full before consent
- Can switch between language versions

### US-4.3: Provide Explicit Consent
**As a** member
**I want to** explicitly consent to a legal document
**So that** my agreement is legally recorded

**Acceptance Criteria:**
- Must explicitly check consent checkbox
- Consent is timestamped
- IP address and user agent recorded
- Content hash captured for integrity
- Cannot modify or delete consent record

### US-4.4: Sync Documents from GitHub
**As a** system administrator
**I want** legal documents to sync from a GitHub repository
**So that** legal team can manage documents via version control

**Acceptance Criteria:**
- Documents auto-sync on schedule
- New versions detected via commit SHA
- Both Spanish and English content synced
- Effective dates respected for activation

### US-4.5: Re-consent on Document Updates
**As a** member
**I want to** be notified when a document I signed is updated
**So that** I can review changes and re-consent

**Acceptance Criteria:**
- Email notification when re-consent required
- Document marked as requiring new consent
- Old consent remains in audit trail
- Grace period before status impact

## Data Model

### LegalDocument Entity
```
LegalDocument
├── Id: Guid
├── Type: DocumentType [enum]
├── Name: string (256)
├── GitHubPath: string (512)
├── CurrentCommitSha: string (40)
├── IsRequired: bool
├── IsActive: bool
├── CreatedAt: Instant
├── LastSyncedAt: Instant
└── Navigation: Versions
```

### DocumentVersion Entity
```
DocumentVersion
├── Id: Guid
├── LegalDocumentId: Guid (FK)
├── VersionNumber: string (50)
├── CommitSha: string (40)
├── ContentSpanish: string [full text]
├── ContentEnglish: string [full text]
├── EffectiveFrom: Instant
├── RequiresReConsent: bool
├── CreatedAt: Instant
└── ChangesSummary: string? (2000)
```

### ConsentRecord Entity (IMMUTABLE)
```
ConsentRecord
├── Id: Guid
├── UserId: Guid (FK → User)
├── DocumentVersionId: Guid (FK → DocumentVersion)
├── ConsentedAt: Instant
├── IpAddress: string (45)
├── UserAgent: string (1024)
├── ContentHash: string (64) [SHA-256]
└── ExplicitConsent: bool [always true for valid records]

⚠️ Database triggers prevent UPDATE and DELETE operations
```

### DocumentType Enum
```
PrivacyPolicy
TermsAndConditions
CodeOfConduct
DataProcessingAgreement
Statutes
```

## Consent Flow

```
┌─────────────────┐
│ View Consents   │
│ Index Page      │
└────────┬────────┘
         │
┌────────▼────────┐
│ Select Document │
│ to Review       │
└────────┬────────┘
         │
┌────────▼────────┐
│ Display Full    │
│ Document Text   │
│ (ES or EN)      │
└────────┬────────┘
         │
┌────────▼────────┐
│ User Reads      │
│ Document        │
└────────┬────────┘
         │
┌────────▼────────┐
│ Check Explicit  │
│ Consent Box     │
└────────┬────────┘
         │
┌────────▼────────────────────┐
│ Create ConsentRecord        │
│ - Timestamp                 │
│ - IP Address               │
│ - User Agent               │
│ - Content Hash (SHA-256)   │
│ - ExplicitConsent = true   │
└────────┬────────────────────┘
         │
┌────────▼────────┐
│ Update Member   │
│ Status (if all  │
│ consents done)  │
└─────────────────┘
```

## Document Sync Process

```
┌──────────────────┐
│ Scheduled Job    │
│ (Daily @ 4 AM)   │
└────────┬─────────┘
         │
┌────────▼─────────┐
│ Fetch from       │
│ GitHub API       │
└────────┬─────────┘
         │
┌────────▼─────────┐
│ Compare Commit   │
│ SHA              │
└────────┬─────────┘
         │
    ┌────┴────┐
    │         │
 [Same]   [Different]
    │         │
 [Skip]  ┌────▼────────┐
         │ Parse New   │
         │ Content     │
         └────┬────────┘
              │
         ┌────▼────────┐
         │ Create New  │
         │ Version     │
         └────┬────────┘
              │
         ┌────▼────────┐
         │ If Updates: │
         │ Batch-Load  │
         │ Affected    │
         │ Members     │
         └────┬────────┘
              │
         ┌────▼───────────┐
         │ Consolidated   │
         │ Notification   │
         │ (1 email per   │
         │ user for all   │
         │ updates)       │
         └────┬───────────┘
              │
         ┌────▼───────────┐
         │ Grace Period   │
         │ Starts (e.g.   │
         │ 7 days)        │
         └────────────────┘
```

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
For each required document:
  1. Get latest version where EffectiveFrom <= now
  2. Check if user has consented to that version
  3. If not, document is "requiring consent"
  4. If RequiresReConsent on new version, old consent invalid
```

### Re-consent Trigger
When a new version is published with `RequiresReConsent = true`:
1. All members with existing consent are notified
2. Their MembershipStatus becomes Inactive
3. They must review and consent to new version
4. Original consent remains in audit trail

## Business Rules

1. **Explicit Consent Only**: Checkbox must be actively checked
2. **Full Document View**: Cannot consent without viewing document
3. **Version Binding**: Consent is to specific version, not document
4. **No Retroactive Changes**: Consent records are immutable
5. **Grace Period**: Members have configurable days to re-consent
6. **Language Choice**: Document available in Spanish (canonical) and English

## Related Features

- [Membership Status](05-membership-status.md) - Status depends on consent completion
- [Background Jobs](08-background-jobs.md) - Document sync and reminder jobs
- [Member Profiles](02-member-profiles.md) - Consent status shown on profile
