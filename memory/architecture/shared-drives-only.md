---
name: Google Drive — Shared Drives only, never My Drive
description: All Google Drive resources live on Shared Drives. API calls must use `SupportsAllDrives = true` and request `permissionDetails`. Only direct permissions are managed; inherited Shared Drive permissions are excluded from drift.
---

**All Google Drive resources are on Shared Drives.** This system does NOT use regular (My Drive) folders.

**Required Drive API conventions:**
- All Drive API calls must use `SupportsAllDrives = true`
- Permission listing must include `permissionDetails` to distinguish inherited from direct permissions
- Only direct permissions are managed by the system — inherited Shared Drive permissions are excluded from drift detection and sync

**Why:** Shared Drives have different ownership, permission inheritance, and quota semantics than My Drive. Mixing the two creates drift between what the system thinks it owns and what's actually on the user's account. Inherited permissions on Shared Drives come from the drive itself (not the file) and aren't ours to manage.

**How to apply:**

- New Drive API calls in `Humans.Infrastructure/Services/GoogleWorkspace/*` always pass `SupportsAllDrives = true`.
- Permission queries pass `Fields = "permissions(id,emailAddress,role,permissionDetails)"`.
- Drift detection / sync logic filters out permissions where `permissionDetails.inherited == true`.
- Don't add features that read from or write to user My Drive folders.

**Operational gates:** Google sync jobs (`SystemTeamSyncJob` hourly, `GoogleResourceReconciliationJob` daily at 03:00) are controlled by per-service mode at `/Admin/SyncSettings` (None/AddOnly/AddAndRemove). Set a service to "None" to disable without redeploying.

**Related:** [`design-rules.md §13`](../../docs/architecture/design-rules.md#13-google-resource-ownership) — Google resource ownership.
