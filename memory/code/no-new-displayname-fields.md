---
name: Never coin a new DisplayName field
description: HARD RULE. Never add a new field/property/parameter named `DisplayName` (or a `*DisplayName` variant) on any new class, interface, struct, record, or method. Pick a concept-specific name — `BurnerName`, `LegalName`, `GroupName`, `TeamName`, `Title`, etc. `User.DisplayName` and the existing `*DisplayName` fields throughout the codebase are legacy debt; do not extend the pattern.
---

`DisplayName` as a field name conflates unrelated concepts (human display names, group titles, role labels, audit-actor labels…) into one bag, which has repeatedly caused PII leaks: a code path expecting a group label receives a user's legal name (because the type system can't tell `string DisplayName` from `string DisplayName`), or vice-versa. The fix is at the naming layer — make the concept impossible to mis-pipe by name alone.

**Why:** Peter's hard rule (PR #671 review). Existing `DisplayName` properties exist in dozens of places (`User`, `UserInfo`, `MemberSummary`, `AdminHumanRow`, `IMailerAudience`, audit DTOs, etc.) and won't be cleaned up retroactively — but every new addition compounds the problem. "If it were up to me, use of the letters DisplayName together would be a compiler error."

**How to apply:**

- New code adds NO field/property/parameter/record-positional with name `DisplayName` or `*DisplayName` (e.g. `UserDisplayName`, `SelectedDisplayName`, `ActorDisplayName`, `SenderDisplayName`).
- Pick the concept-specific name:
  - Human display name (with Profile) → `BurnerName` and render via `<vc:human>` (see [[burnername-is-the-display-name]] for the full DTO-anti-pattern).
  - Human legal name (SEPA/Holded/Profile.FirstName+LastName) → `LegalName`.
  - Audience / mailing list / group label → `GroupName`.
  - Team → `TeamName`.
  - Generic UI label with no person/group concept → `Title` or `Label`.
- Reading from a pre-existing legacy `*.DisplayName` (e.g. `IMailerAudience.DisplayName`, `User.DisplayName`) is allowed — DO NOT cascade-rename across the codebase. Just don't propagate the name onto the NEW type you're writing.
- When forwarding a value from a legacy `DisplayName` source into a new type, the new type's field gets the concept-specific name; the read site does `new MailerAudienceOption(a.Key, GroupName: a.DisplayName)`.
- Pre-existing `DisplayName` fields on existing types are out of scope for any rename — DO NOT propose drive-by renames; ask before touching.

**Enforcement:** None yet (analyzer candidate). Self-enforced via code review.

**Related:** [[burnername-is-the-display-name]] — companion rule for the DTO anti-pattern (`UserId` + name-string in the same VM).
