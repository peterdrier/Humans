<!-- freshness:triggers
  src/Humans.Application/Services/Profile/UserEmailService.cs
  src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Web/Controllers/GoogleController.cs
  src/Humans.Web/Views/Profile/Emails.cshtml
  src/Humans.Web/Views/Google/EmailFlagViolations.cshtml
-->
<!-- freshness:flag-on-change
  Clear / violations service surface, the admin EmailFlagViolations page, and the Profile Emails grid behavior on N>1 violations — review when these change.
-->

# Email Flag Violations — Admin & Self Remediation

Resolves [issue #650](https://github.com/nobodies-collective/Humans/issues/650).

## Business Context

`UserEmail` carries two boolean flags that have invariant constraints across a user's rows:

- **`IsGoogle`** — at most one row per user. Identifies the row that represents the user's Google identity for sync (Groups, Drive). Toggle is exclusive: setting one row clears the previous.
- **`IsPrimary`** (verified) — exactly one row per user when at least one row is verified. The primary row is what receives notifications and what the user is identified by.

Production data showed at least one user with two `IsGoogle = true` rows. The Profile/Admin/Emails grid couldn't recover from the state: its Primary/Google columns rendered as independent per-row radios, and clicking a checked radio doesn't toggle off — the user (and even an admin) had no way to clear an existing flag once duplicates existed.

This feature adds:

1. Service-level recovery methods (`ClearGoogleAsync`, `ClearPrimaryAsync`) that drop a single row's flag without auto-promoting a successor — so the operator can deliberately resolve the duplicate state.
2. UI exposure on the Emails grid (admin always; self only when in violation) so a stuck user can self-recover.
3. A site-wide admin scan page (`/Google/EmailFlagViolations`) that lists every affected user and deep-links to their grid.

## User Stories

### US-46.1: Admin remediates a duplicate-flag state
**As an** admin
**I want to** clear an `IsGoogle` or `IsPrimary` flag from one row of a user with duplicates
**So that** the user's row state matches the invariant again

**Acceptance Criteria:**
- On `/Profile/{id}/Admin/Emails`, every flagged row shows an inline `×` Clear button next to its flag icon.
- Clicking Clear drops only that row's flag — the sibling row's flag stays unchanged.
- `ClearPrimary` does not auto-promote a successor; the admin sets the new primary explicitly.
- An audit row is written (`UserEmailGoogleCleared` / `UserEmailPrimaryCleared`).

### US-46.2: User self-recovers from a stuck-radio state
**As a** member with two `IsGoogle = true` rows in my data
**I want to** clear one of them from my own email page
**So that** I don't have to ask an admin to fix it

**Acceptance Criteria:**
- On `/Profile/Me/Emails`, a Clear button appears on a flagged row only when the user is in an N>1 violation state for that flag (`hasMultipleGoogle` or `hasMultiplePrimary` over verified rows).
- When data is healthy, only the existing exclusive Set Primary / Set Google buttons are shown — no Clear.
- The same `ClearGoogleAsync` / `ClearPrimaryAsync` service methods back self and admin paths; auth is `UserEmailOperations.Edit` (self-or-admin), symmetric with `SetPrimary` / `SetGoogle`.

### US-46.3: Admin scans for invariant violations site-wide
**As an** admin
**I want to** see every user whose email rows violate the IsGoogle / IsPrimary invariants
**So that** I can proactively clean up duplicates before users report being stuck

**Acceptance Criteria:**
- `/Google/EmailFlagViolations` (admin-only) lists every user where:
  - `IsGoogle` count across their rows > 1, OR
  - they have at least one verified row but verified `IsPrimary` count != 1.
- Each row deep-links to that user's `/Profile/{id}/Admin/Emails` for one-click triage.
- Page renders an empty success state when no violations exist.

## Data Model

No new entities or schema changes — the feature operates on existing `UserEmail` rows.

New DTO:

| Type | Field | Purpose |
|------|-------|---------|
| `UserEmailFlagViolation` | `UserId`, `DisplayName`, `IsGoogleCount`, `VerifiedCount`, `VerifiedPrimaryCount`, `HasMultipleGoogle`, `HasPrimaryProblem` | Per-user summary returned by `GetEmailFlagViolationsAsync`. |

New service surface (`IUserEmailService`):

| Method | Semantics |
|--------|-----------|
| `ClearGoogleAsync(userId, userEmailId, actorUserId, ct)` | Drop `IsGoogle` from a single row. Returns false if row not found or not flagged. Does not touch siblings. |
| `ClearPrimaryAsync(userId, userEmailId, actorUserId, ct)` | Drop `IsPrimary` from a single row. Returns false if row not found or not flagged. **Intentionally bypasses `EnsurePrimaryInvariantAsync`** — leaves the user with zero primary so the operator picks the new one explicitly. |
| `GetEmailFlagViolationsAsync(ct)` | Returns the list of users currently violating either invariant, unsorted. The controller sorts for display. |

New audit actions: `AuditAction.UserEmailGoogleCleared`, `UserEmailPrimaryCleared`.

## Routes

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Profile/Me/Emails/ClearGoogle` | POST | self-or-admin | Self-path Clear Google flag (only surfaced in UI on N>1 violation) |
| `/Profile/Me/Emails/ClearPrimary` | POST | self-or-admin | Self-path Clear Primary flag (only surfaced in UI on N>1 violation) |
| `/Profile/{id}/Admin/Emails/ClearGoogle` | POST | self-or-admin | Admin-path Clear Google flag (always surfaced on flagged rows) |
| `/Profile/{id}/Admin/Emails/ClearPrimary` | POST | self-or-admin | Admin-path Clear Primary flag (always surfaced on flagged rows) |
| `/Google/EmailFlagViolations` | GET | admin-only | Site-wide invariant violation scan |

## Cross-Section Dependencies

- **[Profiles section](../sections/Profiles.md)** — owns `UserEmail` and the `EnsurePrimaryInvariantAsync` invariant. `ClearPrimaryAsync` is the deliberate-bypass admin recovery path; document there.
- **[GoogleIntegration section](../sections/GoogleIntegration.md)** — owns the `/Google/*` admin surface; the violations page lives there because Google identity (`IsGoogle`) is the primary invariant under remediation, even though the data is `UserEmail`-shaped.

## Related

- [`docs/features/11-preferred-email.md`](11-preferred-email.md) — base email management feature; the Set Primary / Set Google flow this remediation extends.
- [`memory/code/localization-admin-exempt.md`](../../memory/code/localization-admin-exempt.md) — why `/Google/EmailFlagViolations` uses inline literals rather than resx keys.
- [`memory/architecture/display-sort-in-controllers.md`](../../memory/architecture/display-sort-in-controllers.md) — why violation sort happens in `GoogleController.EmailFlagViolations`, not in the service.
