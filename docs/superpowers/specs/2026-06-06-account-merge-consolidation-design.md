# Account Merge Consolidation — Design

**Date:** 2026-06-06
**Section:** Profiles
**Status:** Design approved (Peter), spec under review

## Problem

Resolving duplicate / mergeable accounts happens through **three** admin surfaces sitting on **two** different merge implementations that don't know about each other:

| Surface | Route | Calls | Engine | Direction |
|---|---|---|---|---|
| Merge Requests | `/Admin/MergeRequests` | `AccountMergeService.AcceptAsync` | `FoldAsync` — `IUserMerge` fan-out (full) | **Fixed by the request** |
| Duplicate Accounts | `/Admin/DuplicateAccounts` | `DuplicateAccountService.ResolveAsync` | **hand-rolled fold** | Admin picks |
| Email Problems | `/Profile/Admin/EmailProblems` | `AccountMergeService.AdminMergeAsync` | `FoldAsync` — fan-out (full) | Admin picks |

Observed symptoms and their root causes:

1. **Merges go the wrong way.** A merge request is auto-created with `Target = whoever just verified the email`, `Source = the older account that already owned it` (`UserEmailService.VerifyEmailAsync`). The `/Admin/MergeRequests` Accept button runs that stored direction with no way to flip it, so verifying a primary email on a secondary account produces a request that archives the *primary*.
2. **The request row is left behind.** `DuplicateAccountService.ResolveAsync` and `AccountMergeService.AdminMergeAsync` fold + tombstone but never touch `AccountMergeRequests`. Only `AcceptAsync`/`RejectAsync` ever close a request, so resolving a pair via the other two pages leaves a dangling Pending row pointing at a now-tombstoned ("Merged User") account.
3. **Two engines silently diverge.** `DuplicateAccountService.ResolveAsync` re-implements the fold by hand and (per its own comment, `DuplicateAccountService.cs:219`) **does not migrate VolunteerHistory/Languages** — so merging the same pair via different pages moves different data. The fan-out (`IUserMerge`) is strictly more complete: it re-FKs Teams, Shifts, Governance, Camps, Campaigns, Tickets, Feedback, Notifications, Roles, **external logins** (`UserService.ReassignAsync → ReassignLoginsToUserAsync`), ContactFields, CommunicationPreferences, **emails** (`UserEmailService.ReassignAsync`), and VolunteerHistory/Languages.
4. **Three doors into one room.** Pure UX duplication on top of (2) and (3).
5. **"Pending email … no longer exists" leaves a half-merge.** `AcceptAsync` commits the fold first (`FoldAsync`, which already tombstones the source), *then* in a **second** transaction marks the pending email verified and flips status. If the pending email is already gone (typically because the same pair was already resolved via another page — symptom 2), the second transaction throws, but the fold already committed: source = "Merged User", request still Pending.

## Decisions (settled)

- **One ordered merge engine.** Atomicity comes from **operation order**, not from a transaction: move the data, then settle the email, then tombstone the archived account **last**. The tombstone is the single observable commit point and the source of truth.
- **No cross-section `TransactionScope`.** Crossing section DB boundaries in one transaction is disallowed (and unreliable on Npgsql/Postgres anyway). The existing wrapping scope in `FoldAsync` is **removed**, replaced by the ordering above. Cross-section work continues to go through each section's own service via `IUserMerge` — never another section's repository.
- **One admin surface.** A single page lists detected duplicates *and* user-submitted requests; the admin **always picks the survivor**. `EmailProblems` keeps its other hygiene checks but loses its own merge button.
- **Delete `DuplicateAccountService.ResolveAsync`** (the lossy hand-rolled fold). `DuplicateAccountService` becomes detection-only.
- **Closed-request audit reading:** reuse the existing **`Rejected`** status for a manual Dismiss; auto-resolved requests become **`Accepted`** with a note. **No new enum value, no row deletion** — GDPR export and audit history stay intact.
- **Self-reconciliation is the cleanup mechanism.** Request state is derived from the archived account's tombstone (`MergedToUserId`); a Pending request whose archived side is already merged is *moot* and is closed on sight. This is what cleans up existing half-done rows — no data migration.

## Target architecture

All of this lives in the **Profiles** section.

### The merge engine — `AccountMergeService.MergeAsync(survivorId, archivedId, adminId, notes, ct)`

This becomes the one and only merge primitive. Ordered, each step committing independently:

1. **Guard.** `survivor != archived`; neither side already tombstoned (existing `AdminMergeAsync` guard on `MergedToUserId`).
2. **Move the data.** For each `IUserMerge`: `ReassignAsync(archived, survivor, admin, now)`. Run **sequentially**, no wrapping `TransactionScope`. Naturally **idempotent** — "move all of archived's X to survivor" is a no-op on re-run because archived has no X left — so a half-completed merge is safely **retryable**.
3. **Settle the email.** Ensure the shared address ends up verified on the survivor and drop the now-redundant pending copy. **A missing/already-consumed pending email is a no-op, never a throw.** Routed through the email-owning service (`UserEmailService`), consistent with the fold already delegating email re-FK to `UserEmailService.ReassignAsync`.
4. **Tombstone the archived account.** `userService.AnonymizeForMergeAsync(archived, survivor, now)` → `MergedToUserId` set, "Merged User". **This is the last observable mutation and the commit point.**
5. **Close the request(s).** Best-effort: set `Status = Accepted` (note: "resolved by merge") on any Pending request for this pair. If this fails, step 6's reconciliation heals it.
6. **Cache invalidation + audit**, after the tombstone (so nothing is published for a merge that didn't complete).

`AcceptAsync` is reframed to: resolve request → `MergeAsync(adminChosenSurvivor, adminChosenArchived, …)`. `AdminMergeAsync` collapses into `MergeAsync`.

**Why this fixes the symptoms:**
- *Symptom 5:* nothing reads as "merged" until step 4; a failure in 2–3 leaves the archived account untouched and the request still actionable — retry. The fatal "pending email gone" throw is removed.
- *Symptoms 2 + existing half-done rows:* the tombstone is the source of truth; reconciliation (below) closes any request whose archived side is already merged.

### The unified surface — `/Admin/AccountMerges` (single controller)

Lists, cross-referenced into one work queue:
- **Detected duplicate pairs** — `DuplicateAccountService.DetectDuplicatesAsync` (unchanged detection).
- **User-submitted requests** — `AccountMergeService.GetPendingRequestsAsync`.
- A pair that has both a detection hit and a pending request shows as one row.
- **Half-done / orphan requests** — a Pending request whose archived side is already tombstoned — surfaced as "already merged" with a one-click **Close**.

Per row: **Merge…** (admin picks survivor → `MergeAsync`) and **Dismiss** (→ `Status = Rejected`, the existing `RejectAsync` mechanism, which already no-ops gracefully when the pending email is gone).

`/Admin/MergeRequests` and `/Admin/DuplicateAccounts` are retired into this one route; the admin nav collapses three entries to one. `EmailProblems` keeps its other 8 `EmailProblemKind` checks; its `SharedAcrossUsers` case links to `/Admin/AccountMerges` instead of carrying its own `EmailProblems/Merge` action.

### Reconciliation / cleanup

- On listing: any Pending request whose archived (Source) account has `MergedToUserId != null` is treated as resolved → shown as "already merged", lazily flipped to `Accepted` (or closed on the admin's click). This clears **existing prod half-done rows** (symptom 5 wreckage) and orphans (symptom 2) without a data migration.
- On merge: step 5 closes any other Pending requests for the same pair.

## Coverage / acceptance criteria

The one engine + one surface must cover all three entry scenarios Peter called out:

1. **Duplicate case** — admin-detected pair, admin picks survivor, full fan-out fold, request (if any) auto-closed.
2. **Merge-requested case** — user-initiated request, admin picks survivor (can flip the auto-assigned direction), request closed.
3. **Cleanup of old half-done merge requests** — a Pending request whose archived side is already "Merged User" can be closed from the unified page (and is recognized as already-merged on listing).

Plus the original symptoms: (1) no forced-wrong-direction merges; (2) no orphan request rows; (3) no engine divergence / data loss; (4) one surface; (5) no half-merge from a missing pending email.

## What changes

- **`AccountMergeService`** — add `MergeAsync` (ordered, no wrapping scope); reframe `AcceptAsync` onto it with an admin-chosen survivor; collapse `AdminMergeAsync`; remove the fatal missing-email throw; route email-settle through `UserEmailService`; add per-pair request reconciliation.
- **`DuplicateAccountService`** — delete `ResolveAsync`; keep `DetectDuplicatesAsync` / `GetDuplicateGroupAsync`.
- **Web** — new `/Admin/AccountMerges` controller + view (merge queue: duplicates + requests + orphans); retire `AdminMergeController` and `AdminDuplicateAccountsController` routes; drop `EmailProblems/Merge` and link `SharedAcrossUsers` to the new page; collapse `AdminNavTree` entries 3→1.
- **No new `AccountMergeRequestStatus` value. No EF data migration.** (Schema unchanged.)

## Risks / notes for the implementer

- **Idempotency is load-bearing.** Each `IUserMerge.ReassignAsync` must be safe to re-run after a partial failure. Audit the fan-out implementations for any non-idempotent step before relying on retry. (Most are "re-FK all of source's rows to target", which is inherently idempotent.)
- **Interim split state** between steps 2 and 4 (some data under survivor, archived not yet tombstoned) is acceptable at ~500-user / single-server scale and self-heals on retry. The never-block-sign-in rule is preserved (archived account isn't tombstoned until the end; logins already point at survivor once step 2 runs).
- **Email-settle ownership.** Confirm the email verify/remove goes through `UserEmailService` rather than `AccountMergeService` reaching `IUserRepository` directly, to keep the merge orchestrator off another owner's repository surface.
- **Removing the fold's `TransactionScope`** is a behavioral change to the *existing* accepted-merge path, not just new code — call it out in the PR.
- Detection logic is duplicated between `DuplicateAccountService.DetectDuplicatesAsync` and `EmailProblemsService` (`SharedAcrossUsers`). De-duplicating that is desirable but **out of scope** here unless trivial; note it, don't force it.
