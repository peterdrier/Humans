---
name: isgoogle-user-controlled
description: "The Google Workspace identity (GoogleIntegration `google_workspace_identities`) is user-set — never write it from merges/syncs/dedup; defaults resolve at read time."
---

A member's Google Workspace identity — GoogleIntegration's `google_workspace_identities` row, formerly `UserEmail.IsGoogle` (frozen since Users decomposition commit 15) — is user-controlled: only the member or an admin designates it (`IWorkspaceIdentityManagement.SetAsync`, from the Emails grid's Google column) or provisioning a @nobodies.team mailbox does.

- **Never write it during merges, syncs, account folds, or duplicate resolution.** A merge deletes the archived account's row and leaves the survivor's alone; a sync writes only the verdict (`SetStatusFromSyncAsync`, for the attempted address), never the address.
- **Defaults are resolved at read time, never stamped.** With no usable designation, `GetWorkspaceIdentityAsync` picks a verified @nobodies.team address, else the verified primary, else the first verified address. A verified @nobodies.team address outranks a non-@nobodies designation. Don't reintroduce an "invariant helper" that writes a winner after each email mutation.
- **An address leaving the account clears the designation** (`IGoogleIntegrationWrite.ClearIfAddressAsync`, a cleared row so the frozen legacy flag can't resurrect it) — the only automatic write.

**Why:** the user's choice is sticky; merges and syncs that overrode user-set state were the original bug. The old add-row invariant helper (`EnsureGoogleInvariantAsync`) became a read-time resolution when the identity moved to GoogleIntegration.

**How to apply:** designing a merge/sync/repair flow → never touch the identity. An address-removal flow → call `ClearIfAddressAsync` after the commit. If unsure which category applies, ask.
