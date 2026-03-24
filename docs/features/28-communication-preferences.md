# Feature 28: Communication Preferences

## Business Context

The platform sends various types of emails: system notifications, event operations, community updates, and marketing/campaign emails. Humans need granular control over which categories they receive, with CAN-SPAM and GDPR-compliant unsubscribe flows that work without login.

This replaces the previous boolean-only approach (`UnsubscribedFromCampaigns`, `SuppressScheduleChangeEmails`) with a proper per-category preference model.

## Message Categories

| Category | Default | Opt-outable | Examples |
|----------|---------|-------------|----------|
| System | On | No | Account, consent, security, welcome, verification |
| EventOperations | On | Yes | Shift changes, schedule updates, team additions, board digest |
| CommunityUpdates | Off | Yes | Facilitated messages, community news |
| Marketing | Off | Yes | Campaign codes, promotions |

## Data Model

### CommunicationPreference Entity

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK to User |
| Category | MessageCategory (string) | Enum stored as string |
| OptedOut | bool | true = user opted out |
| UpdatedAt | Instant | Last change timestamp |
| UpdateSource | string (100) | "Profile", "MagicLink", "OneClick", "DataMigration", "Default" |

**Unique constraint:** `(UserId, Category)` — one row per user per category.

Defaults are created lazily on first access via `GetPreferencesAsync()`.

### MessageCategory Enum

`System = 0`, `EventOperations = 1`, `CommunityUpdates = 2`, `Marketing = 3`

## Unsubscribe Flows

### Magic-Link Unsubscribe (Browser)

1. Email footer contains an unsubscribe link: `/Unsubscribe/{token}`
2. Token is DataProtection-encrypted, time-limited (90 days), payload: `{userId}|{category}`
3. GET shows confirmation page with category name
4. POST confirms unsubscribe, updates preference, logs audit entry

### RFC 8058 One-Click Unsubscribe (Email Client)

1. Outgoing emails include `List-Unsubscribe` and `List-Unsubscribe-Post` headers
2. Email clients can POST to `/Unsubscribe/OneClick` with the token
3. No anti-forgery token required (comes from email client, not browser)
4. Returns HTTP 200 on success

### Legacy Backward Compatibility

Old `CampaignUnsubscribe` protector tokens (from pre-existing campaign emails) are still accepted. They are decoded as the Marketing category.

## Profile Management

Route: `/Profile/Notifications` (authenticated)

Displays all four categories as checkboxes:
- System: always on, disabled (cannot opt out)
- EventOperations, CommunityUpdates, Marketing: toggleable

Changes are persisted via `ICommunicationPreferenceService.UpdatePreferenceAsync()` and audit-logged.

## Email Integration

### Header Injection

Opt-outable emails include:
- `List-Unsubscribe: <{baseUrl}/Unsubscribe/{token}>` (RFC 8058)
- `List-Unsubscribe-Post: List-Unsubscribe=One-Click`

### Footer Link

Email template footer includes "Unsubscribe from these emails" link for opt-outable categories.

### Preference Checking

Before queueing an opt-outable email, the service checks if the user has opted out. If opted out, the email is silently suppressed and logged.

## Audit Trail

All preference changes are recorded as `AuditAction.CommunicationPreferenceChanged` with:
- Entity type: "User", entity ID: userId
- Description includes category name, action (opted in/out), and source

## Migration from Legacy Fields

The old `User.UnsubscribedFromCampaigns` and `User.SuppressScheduleChangeEmails` fields remain on the User entity but are deprecated. A data migration seeds `CommunicationPreference` rows from these existing values. New code reads exclusively from the `CommunicationPreference` table.

## Related Features

- **Feature 22 (Campaigns):** Campaign send wave now uses preference service instead of direct `UnsubscribedFromCampaigns` check, and includes RFC 8058 headers
- **Issue #205 (AccountType):** CommunicationPreference is user-level, independent of account type
- **Issue #200 (MailerLite sync):** Preference table provides query surface for syncing opt-out status

## Routes

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| /Profile/Notifications | GET | Yes | View preferences |
| /Profile/Notifications | POST | Yes | Update preferences |
| /Unsubscribe/{token} | GET | No | Confirmation page |
| /Unsubscribe/{token} | POST | No | Confirm unsubscribe |
| /Unsubscribe/OneClick | POST | No | RFC 8058 one-click |
