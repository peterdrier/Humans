---
name: Tier is derived from approved Application state, not a Profile concept
description: Tier (Colaborador/Asociado) is derived from an Approved Application with an active term — no tier-locking domain rule; the only boundary is no-duplicate-Submitted on `SubmitAsync`.
---

A human exists. They either have an open (`Submitted`) Application or they don't. An `Approved` Application with an active term *is* what makes someone a Colaborador or Asociado — there is no parallel "tier" state for the Application section to negotiate with.

The initial-setup profile form (`Views/Profile/Edit.cshtml`) no longer hosts the application: it describes the tiers and links to `/Governance/Applications/Create` (Users decomposition commit 11).

**What this rules out:**

- A `tier-locking` domain interface or service method. The `isTierLocked` boolean that lived on `IProfileService.GetProfileEditDataAsync` was a UI signal for "should the radios disable?" — derived from "does the user have a Submitted/Approved Application?". That's view-composition logic; it belongs in the controller alongside the `IApplicationDecisionService` call, not as a Profile-domain query.
- An `ITierLockQuery` / `IUserApplicationsQuery` foundational interface that exists only so ProfileService can answer a tier question. The question is the controller's question; ProfileService doesn't need to ask.
- A "Profile concern: can the user edit their tier?" framing. Tier isn't a Profile-edit operation — submitting an Application is. The form just hosts both because the same submission is convenient during onboarding.

**Why:** Peter corrected this during the #685 design pass on 2026-05-09. An earlier interface-extraction proposal (`ITierLockQuery.IsUserTierLockedAsync(userId) → bool`) was rejected as a symptom-level fix that codified the fictional concept rather than removing it. The same pattern as `feedback_fix_means_fix_not_swap.md` — don't trade one shortcut for another.

**How to apply:**

- "Can this user start a new tier application?" — answer at the call site by calling `IApplicationDecisionService.GetUserApplicationsAsync` (or letting `SubmitAsync` reject with `AlreadyPending`). Don't add a tier-state query method to a foundational interface.
- "What tier is this user?" — derived. Governance's `governance_membership_tiers` row is a write-cache populated by approval and expiry (read via `IGovernanceService.GetMembershipTierAsync`); it isn't user-editable, and it isn't authoritative; the authoritative answer is "do they have an active Approved Application of tier X?". The legacy `Profile.MembershipTier` column is frozen.
