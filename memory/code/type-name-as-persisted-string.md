---
name: Never rename a type whose name is persisted or used as a lookup key
description: `nameof(T)` written to a DB column, and `Enum_{typeof(T).Name}_*` resx keys, turn a CLR rename into a silent data or translation break — build green, tests green, no exception. Read before renaming any entity, enum, or DTO, and always before a G5 section prefix drop.
---

A CLR type name is inert only where nothing outside the compiler reads it. Two places in this
codebase read it, and **both fail silently** — green build, green tests, no exception, a 200 response.

1. **Audit `EntityType` discriminators.** Services write an entity-type string to `audit_log` and
   later filter it by exact equality (`e.EntityType == entityType`). Written as `nameof(Product)`, a
   rename changes what the code writes *and* what it queries in one move, so every row already in the
   database becomes unreachable. Tests that assert `nameof` rename themselves in lockstep and stay
   green.
2. **`Enum_{TypeName}_{Value}` resource keys.** `EnumDisplay` looks up
   `Enum_{typeof(TEnum).Name}_{value}`. Rename the enum without renaming the keys and every
   non-English locale falls back to the humanized English value — no missing-key error, because the
   fallback *is* the designed behaviour.

**Rule:** a type name that is persisted, or used to build a lookup key, is a **contract with existing
data**. Pin it as a literal; never regenerate it from `nameof` or `typeof(T).Name`.

**Why:** these are the two rename traps that survive the rendered-HTML diff that guards a G5 section
move — an emptied audit panel renders as an empty panel, and the capture locale is English. Both bit
the Store pilot (nobodies-collective/Humans#866, PR peterdrier/Humans#1223) and both were caught by
review, not by the suite.

**How to apply:** before renaming any entity, enum or DTO:

```bash
grep -rn "nameof(<Type>)" src/          # then ask what reads that string
grep -rn "Enum_<Type>" src/**/*.resx
```

If a hit is persisted or key-forming, declare the value as a `const string` holding its **existing**
value — see `src/Sections/Humans.Store/Services/AuditEntityTypes.cs`, whose constants read
`"StoreProduct"` while the type is now `Product` — and point the tests at the constants so they pin
the contract instead of following the next rename. Resource keys must be renamed in all six language
files in the same commit as the type. Never write a backfill to "fix" old rows; the old rows were
never wrong ([`no-data-backfills`](../process/no-data-backfills.md)).

Same family as [`no-rename-serialized-fields`](no-rename-serialized-fields.md), which covers property
names inside stored JSON; this one covers the *type* name itself.
