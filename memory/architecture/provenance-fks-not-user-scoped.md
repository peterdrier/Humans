---
name: Provenance FKs are not user-scoped data
description: A section's tables can carry user FK columns (AddedByUserId, RecordedByUserId, IssuedByUserId) without the section being user-scoped. Don't reflexively demand IUserDataContributor whenever you see a per-user FK.
type: architecture
---

A section's owned tables can carry user FK columns that record *who performed an action* (`AddedByUserId`, `RecordedByUserId`, `IssuedByUserId`, `CreatedByUserId`, etc.) without the section's data being user-scoped under design-rules §8a. The §8a obligation to implement `IUserDataContributor` only fires when the rows themselves *belong to* the user.

**Test:** if you delete the user, do their rows go with them, or do they belong to a different aggregate (a camp, a team, an event) and merely lose their actor reference? If the latter, the section is not user-scoped — those FKs are provenance/audit, not ownership. The data flows out of GDPR export through the audit log, not through a section-level contributor.

The **Store** section is the canonical example: store orders, lines, payments, and invoices belong to the camp season, not to the lead who clicked the button. Adding `IUserDataContributor` to `StoreService` would have produced incorrect output (someone else's camp's orders showing up in a user's GDPR export).

**Why:** Design-rules §8a was authored before any section had heavy provenance FKs without ownership semantics, and reviewers (humans and bots) initially read "any per-user FK column" as the trigger. Peter corrected that read for the Store section on PR #373; this rule disambiguates the §8a check for future sections.

**How to apply:**
- When a §8 review flags a section for missing `IUserDataContributor` because of per-user FKs, first apply the deletion test above. If the rows belong to a non-user aggregate, the flag is a false alarm — point to design-rules §8a "Provenance FKs are not user-scoped data" paragraph and decline.
- When *adding* a section: only implement `IUserDataContributor` if rows in the section are personally owned by the user (Profiles, Camp memberships, Shift signups, Tier applications, Issues you reported, Feedback you submitted). Provenance-only FKs do not qualify.
