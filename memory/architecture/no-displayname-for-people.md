---
name: No DisplayName for people in code we own
description: HARD RULE. In any interface / DTO / view-model / property / method / parameter we own, person identity is spelled `BurnerName`, never `DisplayName`. The canonical resolution is `BurnerName => !string.IsNullOrWhiteSpace(profile.BurnerName) ? profile.BurnerName : user.DisplayName`. The only legitimate `DisplayName` for a person is `User.DisplayName` on the domain entity (legacy auth-provider field) and the `Name` claim in `HumansUserClaimsPrincipalFactory`. Non-person `DisplayName` labels (groups, locations, resources, guide sections, tier labels) are fine.
---

In any interface / DTO / view-model / property / method / parameter we own, **never** call a person's name "DisplayName". Use `BurnerName`.

**Why:** "DisplayName" reads like the canonical thing to render, so it gets pulled into UI, emails, notifications by reflex — and that's a PII leak (`User.DisplayName` is the legacy auth-provider name, not what the human chose). Issue #692 fixed the leak; this rule prevents the next one. Removing the name from owned surfaces forces the call site to ask "which name?" — and the answer is always `BurnerName`.

**The canonical resolution.** When you need the rendered name for a person and have both refs in scope:

```csharp
public string BurnerName => !string.IsNullOrWhiteSpace(profile.BurnerName) ? profile.BurnerName : user.DisplayName;
```

When the surface carries flat data (a DTO, a view-model row), apply that expression at the construction boundary and store the result as `BurnerName`.

**How to apply:**

- New DTO/VM property for a person's name → `BurnerName` (qualify when needed: `UserBurnerName`, `ActorBurnerName`, `ChangedByBurnerName`, `MatchedBurnerName`).
- Rename method/parameter/local that names a person → `BurnerName` / `burnerName`.
- Razor `Model.DisplayName` for a person → `Model.BurnerName`.

**Carve-outs (these stay as `DisplayName`):**

- `User.DisplayName` — domain entity, legacy auth-provider field, exempt by [`burnername-is-the-display-name`](burnername-is-the-display-name.md).
- `HumansUserClaimsPrincipalFactory` Name claim — legacy claim semantics tied to the auth identity.
- Non-person labels: `DomainGroupInfo.DisplayName` (Google group), `LocationProfileInfo.DisplayName` (location), `BoardDigestTierGroup.DisplayNames` (tier label), `GuideSidebarEntry.DisplayName` (guide section), `ResourceSyncDiff.DisplayName` (resource). The rule is about person identity, not arbitrary labels.

**Related:** [`burnername-is-the-display-name`](burnername-is-the-display-name.md) — write-through-sync rule that keeps `User.DisplayName == Profile.BurnerName` after any save, and the four legitimate `User.DisplayName` read sites.
