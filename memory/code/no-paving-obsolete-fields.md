---
name: no-paving-obsolete-fields
description: When migrating a read/write, switch to the canonical replacement — never carry an `[Obsolete]` field/predicate into new code (e.g. `Profile.IsSuspended` → `State`).
---

When you migrate a caller to a new service, read model or interface, switch it to the canonical
replacement for any legacy field, predicate or helper it used, in the same change. New code is never
written against an obsolete member, even when it "still works".

**Why:** migrations are the only realistic window to retire a legacy member. If new and freshly migrated
code keeps reading it, it never reaches zero callers and the codebase keeps two sources of truth.
Peter: *"stop using the damn obsolete field. migrate to the replacement, don't pave the cow path."*

**How to apply:**
- An `[Obsolete]` attribute or a `#pragma warning disable HUM_*_OBSOLETE` is a stop sign for the line
  under it: replace it with the successor, don't propagate it.
- Known successors: suspension and stub state → `User.State` / `UserInfo.IsSuspended` / `IsStub`;
  "has the name been filled in" → `UserInfo.HasRequiredNameFields`
  ([`derived-predicates-on-userinfo`](../architecture/derived-predicates-on-userinfo.md)); a name for
  display → `UserInfo.BurnerName` / `<vc:human>`
  ([`burnername-is-the-display-name`](../architecture/burnername-is-the-display-name.md)).
- No canonical replacement to be found? Stop and ask. Don't invent one and don't suppress the warning.
- Tests too: a test ported off an obsolete field seeds the new field.

**Exception:** Identity/framework interop that must use the legacy column (claims transformation,
`UserManager.UpdateAsync`).
