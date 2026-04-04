# Communication Preferences

## Business Context

GDPR and CAN-SPAM compliance require giving users control over which communications they receive. The Communication Preferences page lets users manage their email and in-app alert preferences at a granular category level.

## Categories

| Category | Email | Alert | Default | Editable |
|---|---|---|---|---|
| System (account, consent, security) | Always on | Always on | On | No |
| Campaign Codes (discount codes, grants) | Always on | Always on | On | No |
| Facilitated Messages (user-to-user via Humans) | Opt-in | Opt-in | On | Yes |
| Ticketing — [year] (purchase confirmations, event info) | Opt-in / Locked | Opt-in / Locked | On | Conditional |
| Volunteer Updates (shift changes, schedule updates) | Opt-in | Opt-in | On | Yes |
| Team Updates (Drive permissions, member adds/removes) | Opt-in | Opt-in | On | Yes |
| Governance (board voting, tier applications, role assignments) | Opt-in | Opt-in | On | Yes |
| Marketing (mailing list, promotions) | Opt-in | Opt-in | Off | Yes |

### Always-On Categories

System and Campaign Codes are always locked on — users cannot opt out. These categories cover critical account operations and code delivery.

### Ticketing Locking

When a user has a matched `TicketOrder` (auto-matched by email), their Ticketing preference is locked on. Purchase confirmations and event information are mandatory for ticket holders. Users without ticket orders can opt in/out freely.

### Facilitated Messages Opt-Out

When a user opts out of Facilitated Messages, the "Send Message" button is hidden from their profile card, and the `/Profile/{id}/SendMessage` action redirects with an error message. This prevents other users from sending messages to someone who doesn't want them.

## Data Model

**Entity:** `CommunicationPreference` (table: `communication_preferences`)
- `UserId` + `Category` (unique index) — one row per user per category
- `OptedOut` (bool) — true = user does NOT receive email for this category
- `InboxEnabled` (bool) — true = user receives in-app alerts for this category
- `UpdatedAt` (Instant) — when preference was last changed
- `UpdateSource` (string) — how it was set: "Profile", "MagicLink", "OneClick", "Default", "DataMigration"

**Enum:** `MessageCategory` — stored as string in DB
- Active: System, CampaignCodes, FacilitatedMessages, Ticketing, VolunteerUpdates, TeamUpdates, Governance, Marketing
- Deprecated: EventOperations (→ VolunteerUpdates + TeamUpdates), CommunityUpdates (→ FacilitatedMessages)

## Routes

- `GET /Profile/Me/CommunicationPreferences` — view/edit preferences
- `POST /Profile/Me/CommunicationPreferences` — save preferences
- `GET /Profile/Me/Notifications` — permanent redirect to above (backwards compat)

## Migration History

- `SplitCommunicationCategories` — data migration splitting EventOperations → VolunteerUpdates + TeamUpdates, renaming CommunityUpdates → FacilitatedMessages

## Related Features

- [Notification Inbox (#292-295)](notification-inbox.md) — in-app alerts controlled by the Alert column
- Unsubscribe flow — RFC 8058 one-click + browser-based unsubscribe using category-aware tokens
- Facilitated Messaging — user-to-user email via Humans, gated by FacilitatedMessages preference
