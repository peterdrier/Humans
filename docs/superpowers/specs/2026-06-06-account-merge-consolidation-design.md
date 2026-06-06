# Account Merge Consolidation — Design

**Date:** 2026-06-06
**Section:** Users. The merge acts on User accounts, external logins, and emails; route prefix `/Users/Admin`. As part of this change, `AccountMergeService` / `DuplicateAccountService` (+ their interfaces and `AccountMergeRepository`) move from the `Profiles` namespace into `Users` — see *Namespace move* under What changes.
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
- **One detector.** "Two accounts share a normalized email" is currently computed four ways (`DuplicateAccountService.DetectDuplicatesAsync` + `GetDuplicateGroupAsync`, `EmailProblemsService.SharedAcrossUsers` + `UsersShareAnyEmailAsync`). Collapse to a single detector behind the unified page, and **drop the `SharedAcrossUsers` kind from the EmailProblems scan** — the unified page is its home; EmailProblems links there.
- **Proportional namespace move.** `AccountMergeService`, `DuplicateAccountService`, their interfaces, and `AccountMergeRepository` (+ interface + DI registration) move `Profiles → Users`; the new controller/VMs are created in `Users` from the start. `EmailProblemsService` / `UserEmailService` stay in `Profiles`. This also removes a latent coupling — both services already use the Users-owned `IUserRepository` from a `Profiles` home. Done as a **Peter-driven ReSharper move, sequenced first** (agents do not author the rename).

## Target architecture

All of this is the **Users** section.

### The merge engine — `AccountMergeService.MergeAsync(survivorId, archivedId, adminId, notes, ct)`

This becomes the one and only merge primitive. Ordered, each step committing independently:

1. **Guard.** `survivor != archived`; neither side already tombstoned (existing `AdminMergeAsync` guard on `MergedToUserId`).
2. **Move the data.** For each `IUserMerge`: `ReassignAsync(archived, survivor, admin, now)`. Run **sequentially**, no wrapping `TransactionScope`. Naturally **idempotent** — "move all of archived's X to survivor" is a no-op on re-run because archived has no X left — so a half-completed merge is safely **retryable**.
3. **Settle the email.** The fan-out's `UserEmailService.ReassignAsync` (→ `UserRepository.ReassignUserEmailsToUserAsync`) already **collapses same-address rows onto the survivor and OR-combines `IsVerified`**, so an exact-address match is verified by step 1. The only residual is the gmail-normalized-but-not-identical case: mark the request's pending email verified. Post-move `AccountMergeService` lives in `Users`, so the `UserEmail` table's `IUserRepository` is its own section — this stays a **direct `MarkUserEmailVerifiedAsync` call**, just made **non-fatal**: a missing/already-consumed pending email is a no-op, never a throw.
4. **Tombstone the archived account.** `userService.AnonymizeForMergeAsync(archived, survivor, now)` → `MergedToUserId` set, "Merged User". **This is the last observable mutation and the commit point.**
5. **Close the request(s).** Best-effort: set `Status = Accepted` (note: "resolved by merge") on any Pending request for this pair. If this fails, step 6's reconciliation heals it.
6. **Cache invalidation + audit**, after the tombstone (so nothing is published for a merge that didn't complete).

`AcceptAsync` is reframed to: resolve request → `MergeAsync(adminChosenSurvivor, adminChosenArchived, …)`. `AdminMergeAsync` collapses into `MergeAsync`.

**Why this fixes the symptoms:**
- *Symptom 5:* nothing reads as "merged" until step 4; a failure in 2–3 leaves the archived account untouched and the request still actionable — retry. The fatal "pending email gone" throw is removed.
- *Symptoms 2 + existing half-done rows:* the tombstone is the source of truth; reconciliation (below) closes any request whose archived side is already merged.

### The unified surface — `/Users/Admin/AccountMerges` (single controller)

`UsersAdminAccountMergesController` at `[Route("Users/Admin/AccountMerges")]`, mirroring the existing `UsersAdminDebugController` convention (`/Admin/*` is a nav holder, not a section — routes are section-prefixed).

Lists, cross-referenced into one work queue:
- **Detected duplicate pairs** — `DuplicateAccountService.DetectDuplicatesAsync` (unchanged detection).
- **User-submitted requests** — `AccountMergeService.GetPendingRequestsAsync`.
- A pair that has both a detection hit and a pending request shows as one row.
- **Half-done / orphan requests** — a Pending request whose archived side is already tombstoned — surfaced as "already merged" with a one-click **Close**.

Per row: **Merge…** (admin picks survivor → `MergeAsync`) and **Dismiss** (→ `Status = Rejected`, the existing `RejectAsync` mechanism, which already no-ops gracefully when the pending email is gone).

`/Admin/MergeRequests` and `/Admin/DuplicateAccounts` are retired into this one route; the "Members" nav group collapses "Merge requests" + "Duplicate detection" into one "Account merges" entry. `EmailProblems` keeps its other 8 `EmailProblemKind` checks but **`SharedAcrossUsers` is dropped from the scan** (the unified page owns shared-email pairs); the page links to `/Users/Admin/AccountMerges` for that case, and `EmailProblems/Merge` + `EmailProblemsCompare` are removed.

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

### Namespace move (Peter-driven ReSharper, sequenced first)

`AccountMergeService`, `DuplicateAccountService`, `IAccountMergeService`, `IDuplicateAccountService`, `IAccountMergeRepository`, `AccountMergeRepository` → `Users` namespace/folders; the `AddSingleton<IAccountMergeRepository, …>` registration moves from `ProfileSectionExtensions` to `UsersSectionExtensions`. Because a service's repository must share its section, the repository moves with the service (leaving it in `Profiles` would be a Users-service → Profiles-repo violation). The new controller/VMs are authored directly in `Users`.

### Engine & services

- **`AccountMergeService`** — add `MergeAsync(survivor, archived, …)` (ordered, no wrapping scope); reframe `AcceptAsync` onto it with an admin-chosen survivor; collapse `AdminMergeAsync` into it; remove the fatal missing-email throw; route email-settle through `UserEmailService`; add per-pair request reconciliation. Net surface shrinks (three merge entrypoints → one).
- **`DuplicateAccountService`** — **delete `ResolveAsync`** (lossy hand-rolled fold) and shed the deps it leaves dead (`userRepository`, `auditLogService`, `userInfoInvalidator`, `clock`); keep detection (`DetectDuplicatesAsync`, `GetDuplicateGroupAsync`) — feed the one shared detector.
- **`EmailProblemsService`** — drop `SharedAcrossUsers` from `ScanAsync` and remove `UsersShareAnyEmailAsync`; the duplicate-pair concept now has one owner.

### Web

- **New** `UsersAdminAccountMergesController` at `/Users/Admin/AccountMerges` + view: one work queue of detected duplicates + user requests + half-done/orphan rows, each with **Merge…** (pick survivor) and **Dismiss**.
- **Retire** `AdminMergeController` + `AdminDuplicateAccountsController` (controllers, views, routes).
- **Trim** `ProfileAdminController` — remove `Merge`, `EmailProblemsCompare`, `ResolveAndValidateMergePairAsync`, and the views/deps they used; `EmailProblems` links to the new page for shared-email pairs.
- **Remove dead view models** — `AccountMerge{List,Request,Detail}` + `DuplicateAccount{List,Group,Item,Detail,EmailRow}` → one unified VM set. **Reuse** the shared `ProfileSummaryViewModel` for account cards (used in 11 places — do not fork it).
- **Nav** — "Members" group collapses "Merge requests" + "Duplicate detection" → one "Account merges" entry; "Email problems" stays.

**No new `AccountMergeRequestStatus` value. No EF data migration.** (Schema unchanged.)

## Risks / notes for the implementer

- **Idempotency is load-bearing.** Each `IUserMerge.ReassignAsync` must be safe to re-run after a partial failure. Audit the fan-out implementations for any non-idempotent step before relying on retry. (Most are "re-FK all of source's rows to target", which is inherently idempotent.)
- **Interim split state** between steps 2 and 4 (some data under survivor, archived not yet tombstoned) is acceptable at ~500-user / single-server scale and self-heals on retry. The never-block-sign-in rule is preserved (archived account isn't tombstoned until the end; logins already point at survivor once step 2 runs).
- **Email-settle is same-section post-move.** Because `AccountMergeService` moves to `Users`, the `UserEmail` table's `IUserRepository` is its own section — the settle stays a direct `MarkUserEmailVerifiedAsync` call (no `UserEmailService` indirection); the only behavioral change is dropping the throw.
- **Removing the fold's `TransactionScope`** is a behavioral change to the *existing* accepted-merge path, not just new code — call it out in the PR.
- **Sequencing.** The namespace move runs **first** (Peter, ReSharper) so new code lands in `Users` and agents never author the rename; engine + surface work follows. The plan must make this an explicit gate, not a mid-stream step.
- **Route references.** Retiring two controllers means updating inbound links and any tests/e2e that hit `/Admin/MergeRequests` or `/Admin/DuplicateAccounts`.
- **Section invariant docs.** Moving the services to `Users` may require touching `docs/sections/` for Users/Profiles — verify against the section invariant docs.

## Out of scope / follow-up

- **Full Users-vs-Profiles boundary reckoning** (option b) — moving `EmailProblemsService` / `UserEmailService` and drawing the real section line. Separate, larger; file an issue if wanted.
- **The other `EmailProblems` hygiene kinds** (ghost logins, legacy identity, orphan emails, multiple/zero primary/Google) are User-account hygiene that arguably wants a `/Users/Admin` home too, but they're unrelated to merging.
