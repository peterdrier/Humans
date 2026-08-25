---
name: Never rename a type whose name is persisted or used as a lookup key
description: `nameof(T)` written to a DB column, and `Enum_{typeof(T).Name}_*` resx keys, turn a CLR rename into a silent data or translation break — build green, tests green, no exception. Two different remedies: pin the persisted one, rename the resource keys with the type. Read before renaming any entity, enum, or DTO, and always before dropping a section's legacy type-name prefix.
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

**Rule:** find every reader of the name before renaming — then apply the remedy that fits which kind
of reader it is. The two are **not** the same:

| Reader | Why | Remedy |
|---|---|---|
| **Persisted** — the name is a value in a column, matched later by equality | Rows already in the database carry the old string and cannot be re-emitted. It is a contract with existing data | **Pin it.** Declare a `const string` holding the existing value; never regenerate it from `nameof` / `typeof(T).Name` |
| **Key-forming** — the name builds a lookup key into an asset you ship (`Enum_{typeof(TEnum).Name}_{value}` in the resx set) | The keys are source, not data. They can change atomically with the type, and `EnumDisplay` deliberately derives them | **Rename together.** Rename the keys in all six language files in the same commit as the type. Do *not* pin the type name |

Pinning a resource key would freeze the resx set to a name the code no longer uses, which is worse
than the rename. Pinning a persisted discriminator is the only correct answer, because the database
is not yours to rewrite.

**Why:** these are the two rename traps that survive the rendered-HTML diff that guards a section
move — an emptied audit panel renders as an empty panel, and the capture locale is English. Both bit
the Store pilot and both were caught by review, not by the suite.

**How to apply:** before renaming any entity, enum or DTO, run both searches — as separate lines, and
with a bare prefix rather than a trailing `*`, since `grep`'s default BRE reads `Store*` as "`Stor`
followed by any number of `e`" and would miss `StoreProduct` entirely:

```bash
grep -rn 'nameof(<Type>' src/                        # persisted? then ask what reads that string
grep -rn --include='*.resx' 'Enum_<Type>' src/       # key-forming? --include, not src/**/*.resx
```

**Persisted hit** → declare a `const string` holding its **existing** value; see
`src/Sections/Humans.Store/Services/AuditEntityTypes.cs`, whose constants read `"StoreProduct"` while
the type is now `Product`. Point the tests at the constants so they pin the contract instead of
following the next rename. Never write a backfill to "fix" old rows; the old rows were never wrong
([`no-data-backfills`](../process/no-data-backfills.md)).

**Key-forming hit** → rename the keys alongside the type, in all six language files, in the same
commit. Nothing gets pinned; the resx set stays derived from the live type name, which is what
`EnumDisplay` expects.

Same family as [`no-rename-serialized-fields`](no-rename-serialized-fields.md), which covers property
names inside stored JSON; this one covers the *type* name itself.
