---
name: update-source-attribution
description: When writing `CommunicationPreference.UpdateSource`, reflect actor (signed-in/anon) + channel — don't conflate `Guest` (session) with `MagicLink` (token).
---

The `source` passed to Email's `UpdatePreferenceAsync` names both the **actor** and the **channel**.
Never one label for two channels.

| Source | Actor | Channel |
|---|---|---|
| `"Profile"` | Signed-in user with a profile | `/Email/CommunicationPreferences` |
| `"Guest"` | Signed-in user without a profile | `/Email/Guest/CommunicationPreferences` |
| `"MagicLink"` | Anonymous, valid unsubscribe token | the guest page with `utoken` |
| `"OneClick"` | Anonymous, RFC 8058 List-Unsubscribe-Post | `/Email/Unsubscribe/OneClick` |
| `"Default"` | None; historical lazy seeding, no current writer | |
| `"DataMigration"` | A one-shot data migration | |

**Why:** the audit entry embeds the source ("opted out via {source}"). When someone disputes an
unsubscribe we must be able to tell an authenticated session from a magic link (proves inbox access)
from a mail-client one-click. `"MagicLink"` is the only path with no session, so the one most likely to
be challenged.

**How to apply:**
- `GuestController.ResolveUserIdOrTokenAsync` returns `FromToken`; that bool decides
  `"MagicLink"` vs `"Guest"`. Don't infer it from `utoken != null` (a session request can carry a stale
  token).
- A new endpoint that writes preferences gets a new label naming its channel, unless actor and channel
  are identical to an existing label's.
- A test asserting `UpdateSource` must post a value that differs from the category's seeded default:
  the service returns early on a no-op and the row stays `"Default"`.

**Related:** [`Email.md`](../../src/Sections/Humans.Email/Docs/Email.md)
