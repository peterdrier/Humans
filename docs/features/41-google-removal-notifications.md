<!-- freshness:triggers
  src/Humans.Application/Services/GoogleIntegration/GoogleRemovalNotificationService.cs
  src/Humans.Application/Interfaces/GoogleIntegration/IGoogleRemovalNotificationService.cs
  src/Humans.Application/Services/GoogleIntegration/GoogleWorkspaceSyncService.cs
  src/Humans.Application/Services/Email/OutboxEmailService.cs
  src/Humans.Application/Interfaces/Email/IEmailService.cs
  src/Humans.Application/Interfaces/Email/IEmailRenderer.cs
  src/Humans.Infrastructure/Services/EmailRenderer.cs
  src/Humans.Web/Resources/SharedResource*.resx
-->
<!-- freshness:flag-on-change
  Variant selection logic, suppression cases, MessageCategory routing, and resource-name fallback — review when sync removal pathways or email-template wiring changes.
-->

# Google Removal Notifications

## Business Context

When Google Workspace sync removes a user's email address from a Google Group or revokes a Google Drive permission, Google itself sends nothing. Most removals are routine secondary-email cleanup (a Human has multiple `UserEmail` rows; the user or admin flips `IsGoogle` from address A to address B; sync removes A from groups/folders while B keeps full access). A smaller fraction are genuine loss-of-access (role change, team change, account deletion). Both look identical from the recipient's mailbox: silence.

This silence drives support load — *"why am I no longer in the team mailing list?"* — and hides mistakes: when sync removes someone in error, neither the user nor a coordinator finds out until access is missed.

This feature notifies the address that was removed whenever Google sync deletes a Group membership or a Drive permission, with copy that distinguishes "you lost access" from "an old address got tidied up."

Tracked in: peterdrier/Humans#639. Initial implementation: peterdrier/Humans#404.

## User Stories

### US-41.1: Loss-of-access notification (Variant 1)
**As a** user whose Google access was removed by sync
**I want to** receive an email at the affected address explaining what happened
**So that** I can escalate to my team coordinator if the removal was an error

**Acceptance Criteria:**
- Sent only after a confirmed Google API delete (not before)
- Group sub-template surfaces both `{group-name}` and `{group-email}`
- Drive sub-template surfaces `{folder-name}`
- Body explains the action was automatic based on current team / role assignments
- Body directs the user to contact their team coordinator if they believe it was an error (generic phrasing — no per-team coordinator lookup)

### US-41.2: Secondary-email cleanup notification (Variant 2)
**As a** user with multiple Nobodies email addresses
**I want to** be told which of my addresses just lost group/drive access
**So that** I have a record of which address was tidied up and can verify my primary access is unchanged

**Acceptance Criteria:**
- Sent when the removed address resolves to a User who has another verified `IsGoogle` `UserEmail` row not also being removed
- One template covers both Group and Drive removals (the message is reassurance, not resource-specific)
- Body mentions the removed address, confirms the user's current primary Google address, and confirms access through the primary is unchanged
- Body directs the user to contact their team coordinator if the removed address should still have had access

### US-41.3: Localized delivery
**As a** Spanish / Catalan / German / French / Italian / English-speaking member
**I want to** receive removal notifications in my preferred language
**So that** the message is actionable without translation

**Acceptance Criteria:**
- Templates exist in all six supported cultures (`en`, `es`, `de`, `it`, `fr`, `ca`)
- Recipient's `User.PreferredLanguage` selects the culture; missing/blank falls back to `en`

### US-41.4: Suppression for known non-events
**As a** user whose `UserEmail` row was deleted (account anonymization, self-unlink, OAuth-rename-in-place)
**I want to** NOT receive a removal email for an address that no longer belongs to me
**So that** I don't get confusing mail about an identity I no longer have

**Acceptance Criteria:**
- Lookup of the removed address yielding no `UserEmail` row → no email sent (orphan suppression covers deleted-user, self-unlink, and OAuth-rename-in-place)
- All other removals send (including reconciliation drift and `SyncRemovalReason.EmailRotation` Workspace-identity rotations — the rotated-out address gets Variant 2 so the user sees which address was tidied)

## Variant Selection Logic

```
on removal of address E (from group G or folder F):
    user = lookup user by E (via UserEmail.Email, verified rows only)
    if user is null
        → no email (orphan / deleted / OAuth-rename-in-place)
    elif user has another verified UserEmail with IsGoogle=true that is NOT also being removed
        → Variant 2 — secondary-email cleanup
    else
        → Variant 1 — loss of access (Group sub-template or Drive sub-template by resource type)
```

The send target in both variants is the address that was removed — the mailbox owner sees a self-confirmable record that this specific address lost access.

## Suppression Cases

| Case | Detection | Behavior |
|------|-----------|----------|
| Orphan address | `UserEmail.GetUserIdByVerifiedEmailAsync` → null | No email |
| Deleted / anonymized user | Same as orphan (rows removed) | No email |
| User-initiated email unlink via Profile UI | Same as orphan (row deleted before sync) | No email |
| OAuth-rename-in-place | Same as orphan (`EmailRenameDetectionResult` rewrites the row) | No email |
| Workspace identity rotation A → B | `SyncRemovalReason.EmailRotation` (advisory) | Variant 2 sent at A |
| Reconciliation drift | `SyncRemovalReason.Reconciliation` | Variant 1 or Variant 2 per selector |

> **Resource deletion / unlink** (the Group or Folder itself is removed at the Humans side) is currently treated as a normal removal — every member gets a removal email. If this proves too noisy, future iteration could thread a "resource itself is gone" signal through to suppress.

## Email Routing

- All three templates render through `BrandedEmailBodyComposer` for the standard header / footer
- All three are enqueued with `MessageCategory.System` — the existing outbox path suppresses the unsubscribe footer for system-category messages, which matches the spec's intent (action-confirmation notifications are not unsubscribable)
- Templates live in `SharedResource{,.es,.de,.it,.fr,.ca}.resx` keyed `Email_GoogleGroupRemoval_LossOfAccess_*`, `Email_GoogleDriveRemoval_LossOfAccess_*`, `Email_GoogleAccessRemoval_SecondaryCleanup_*`
- Resource-name fallback: if `resourceName` is missing, use `resourceIdentifier` (group email or URL); if both are missing, fall back to `(unknown)` rather than crashing

## Group-Email Derivation

Google Groups don't carry their primary email directly on the resource. We derive it from the resource URL (`https://groups.google.com/a/{domain}/g/{prefix}` → `{prefix}@{domain}`). When the URL is missing or unparseable, we fall back to the resource Name. The Variant 1 group sub-template still renders correctly in both cases — the body just shows the name twice instead of name + email.

## Workflows

```
GoogleWorkspaceSyncService.RemoveUserFromGroupAsync (gateway)
  → IGoogleGroupMembershipClient delete
  → on success: GoogleRemovalNotificationService.NotifyRemovalAsync
     → orphan check
     → variant selection
     → IEmailService.SendGoogle*Async → OutboxEmailService.EnqueueAsync (MessageCategory.System)

GoogleWorkspaceSyncService.RemoveUserFromDriveAsync (gateway)
  → IGoogleDrivePermissionsClient delete
  → on success: same notification flow
```

The notification is **always** post-delete, never pre-delete — we don't tell users about a removal that didn't happen.

## Related Features

- [`07-google-integration.md`](07-google-integration.md) — sync architecture, gateway methods, sync modes
- [`21-email-outbox.md`](21-email-outbox.md) — outbox infrastructure, `MessageCategory`, branded-template composition
- [`02-profiles.md`](02-profiles.md) — `UserEmail` rows, `IsGoogle` flag, email rotation flows
- [`docs/sections/GoogleIntegration.md`](../sections/GoogleIntegration.md) — section invariants, cross-section dependencies

## Open Follow-ups

- **Drive cleanup is deferred to reconciliation.** When a user's Workspace identity rotates, the rotation flow proactively removes the old address from Groups but leaves the old Drive permissions for reconciliation to clean up later. Result is a slight delay between the rotation and the Variant 2 Drive email. Symmetrizing (rotation flow proactively removes Drive permissions too) is on the backlog.
- **Per-team coordinator escalation.** v1 uses generic "your team coordinator" phrasing. A future iteration could surface the actual coordinator name(s) by joining through team-membership data, gated on a feature flag if the join cost is non-trivial.
