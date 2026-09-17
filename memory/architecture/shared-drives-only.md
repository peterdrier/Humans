---
name: Google Drive — Shared Drives only, never My Drive
description: All Google Drive resources live on Shared Drives — API calls need `SupportsAllDrives = true` + `permissionDetails`; inherited access is preserved when managing direct grants.
---

**All Google Drive resources are on Shared Drives.** This system does NOT use regular (My Drive) folders.

**Required Drive API conventions:**
- All Drive API calls must use `SupportsAllDrives = true`
- Permission listing must include `permissionDetails` to distinguish inherited from direct permissions
- Manage direct grants only; preserve inherited Shared Drive access. A mixed permission's direct elevation can be updated down to its inherited floor, but the mixed permission cannot be deleted at the child.

**Why:** Shared Drives have different ownership, permission inheritance, and quota semantics than My Drive. Mixing the two creates drift between what the system thinks it owns and what's actually on the user's account. Inherited permissions come from the Shared Drive or parent folders and aren't ours to manage.

**How to apply:**

- New Drive API calls in `Humans.GoogleIntegration/Services/Workspace/*` always pass `SupportsAllDrives = true`.
- Permission queries pass `Fields = "permissions(id,emailAddress,role,permissionDetails)"`.
- The legacy Teams-keyed reconciler filters out permissions with any inherited component. The source-claimed reconciler retains direct/inherited details, updates direct elevations to at least the inherited floor, and treats floor-only access as satisfied. Neither path deletes inherited or mixed permissions. `AddOnly` never downgrades access.
- Don't add features that read from or write to user My Drive folders.

**Operational gates:** Google sync jobs (`SystemTeamSyncJob` hourly, `GoogleResourceReconciliationJob` daily at 03:00) are controlled by per-service mode at `/Admin/SyncSettings` (None/AddOnly/AddAndRemove). Set a service to "None" to disable without redeploying.

**Related:** [`design-rules.md §13`](../../docs/architecture/design-rules.md#13-google-resource-ownership) — Google resource ownership.
