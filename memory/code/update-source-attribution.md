---
name: UpdateSource attribution — distinguish actor + channel
description: When writing CommunicationPreference (or any audited preference write), `UpdateSource` must reflect how the user reached the endpoint — token-driven anonymous vs. session-driven, and which UI. Don't conflate "Guest" with "MagicLink".
---

When a controller writes a `CommunicationPreference` via `ICommunicationPreferenceService.UpdatePreferenceAsync`, the `source` parameter must reflect both the **actor** (signed-in vs. anonymous) and the **channel** (which UI/endpoint). Don't use one label for two channels.

**The current vocabulary** (also documented in [`docs/sections/Profiles.md`](../../docs/sections/Profiles.md) and [`docs/features/communication-preferences.md`](../../docs/features/communication-preferences.md)):

| Source | Actor | Channel |
|---|---|---|
| `"Profile"` | Signed-in user with a Profile | `/Profile/Me/CommunicationPreferences` |
| `"Guest"` | Signed-in profileless user | `/Guest/CommunicationPreferences` |
| `"MagicLink"` | Anonymous, valid unsubscribe token | `/Guest/CommunicationPreferences/Update?utoken=…` |
| `"OneClick"` | Anonymous, RFC 8058 List-Unsubscribe-Post | `/Unsubscribe/OneClick` |
| `"Default"` | (none — lazy seed by `GetPreferencesAsync`) | first read |
| `"DataMigration"` | Backfill | one-shot data migration |

**Why:** GDPR / CAN-SPAM audit defensibility. The audit-log description literally embeds the source (`"{Category} opted out via {source}"`). When a user disputes "I never unsubscribed", we need to tell whether it came from their authenticated session, a magic-link in their inbox (proves they had inbox access), or one-click from an MUA — those are different evidentiary stories. Conflating them destroys the signal.

The token-driven `"MagicLink"` value also matters because it's the only path that doesn't require an account session, so it's the one most likely to be challenged. PR #387 split `"Guest"` into `"Guest"` (session) + `"MagicLink"` (token) for exactly this reason.

**How to apply:**

- `GuestController.ResolveUserIdOrTokenAsync` returns `(UserId, TokenCategory, FromToken)`. The `FromToken` bool is the source of truth — `fromToken ? "MagicLink" : "Guest"` in `UpdatePreference`. Don't second-guess it from `utoken != null` (the session path can run with a stale `utoken` query param too).
- Adding a new endpoint that writes preferences? Pick a new source label that names the channel, OR reuse an existing one only if the actor + channel are identical to that label's owner.
- Tests that assert `UpdateSource` must POST values that **differ from the seeded default** for the chosen category — the service early-returns on idempotent updates and the row stays at `"Default"`. `MessageCategory.VolunteerUpdates` (default `OptedOut=false`) flipped via `emailEnabled=false` is the canonical assertion shape; see `tests/Humans.Integration.Tests/Controllers/UnsubscribeFlowTests.cs`.

**Related:** [`docs/sections/Profiles.md`](../../docs/sections/Profiles.md) (CommunicationPreference table), [`docs/features/communication-preferences.md`](../../docs/features/communication-preferences.md).
