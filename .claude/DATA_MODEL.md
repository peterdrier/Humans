# Data Model

## Key Entities

| Entity | Purpose | Storage |
|--------|---------|---------|
| User | User accounts (custom IdentityUser with Google OAuth) | Database |
| Profile | Member profile with computed MembershipStatus | Database |
| ContactField | Contact information with per-field visibility controls | Database |
| Application | Membership application with Stateless state machine | Database |
| RoleAssignment | Temporal role memberships (ValidFrom/ValidTo) | Database |
| LegalDocument / DocumentVersion | Legal docs synced from GitHub | Database |
| ConsentRecord | **APPEND-ONLY** consent audit trail | Database |
| Team / TeamMember | Working groups | Database |
| GoogleResource | Drive folder provisioning | Database |

## Relationships

```
User 1──n Profile
User 1──n RoleAssignment
User 1──n ConsentRecord
User 1──n TeamMember

Profile 1──n ContactField

Team 1──n TeamMember
Team 1──n TeamJoinRequest

LegalDocument 1──n DocumentVersion
DocumentVersion 1──n ConsentRecord
```

## ContactField Entity

Contact fields allow members to share different types of contact information with per-field visibility controls.

### Field Types (`ContactFieldType`)

| Value | Description |
|-------|-------------|
| Email | Email address |
| Phone | Phone number |
| Signal | Signal messenger |
| Telegram | Telegram messenger |
| WhatsApp | WhatsApp messenger |
| Other | Custom type (requires CustomLabel) |

### Visibility Levels (`ContactFieldVisibility`)

Lower values are more restrictive. A viewer with access level X can see fields with visibility >= X.

| Value | Level | Who Can See |
|-------|-------|-------------|
| BoardOnly | 0 | Board members only |
| LeadsAndBoard | 1 | Team leads (metaleads) and board |
| MyTeams | 2 | Members who share a team with the owner |
| AllActiveProfiles | 3 | All active members |

### Access Level Logic

Viewer access is determined by:
1. **Self** → BoardOnly (sees everything)
2. **Board member** → BoardOnly (sees everything)
3. **Any metalead** → LeadsAndBoard
4. **Shares team with owner** → MyTeams
5. **Active member** → AllActiveProfiles only

## Serialization Notes

- All entities use System.Text.Json serialization
- See `CODING_RULES.md` for serialization requirements
