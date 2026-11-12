---
name: type-name-as-persisted-string
description: Before renaming a type, grep for `nameof(T)` written to a DB column and `Enum_{TypeName}_*` resx keys — pin the persisted one as a const, rename the resx keys with the type.
---

Two things read a CLR type's name at runtime, and a rename breaks both silently (green build, green
tests, a 200 response). They need opposite remedies.

| Reader | What breaks | Remedy |
|---|---|---|
| **Persisted** — audit `EntityType` written as `nameof(T)` and later matched by equality | Existing rows carry the old string and become unreachable; tests using `nameof` rename in lockstep and stay green | **Pin it.** A `const string` holding the *existing* value (see `src/Sections/Humans.Store/Services/AuditEntityTypes.cs`); point tests at the const |
| **Key-forming** — `EnumDisplay` looks up `Enum_{TypeName}_{Value}` in resx | Every non-English locale falls back to the humanized English value, with no missing-key error | **Rename together.** Rename the keys in every culture in the same commit. Don't pin |

**How to apply:** before renaming any entity, enum or DTO:

```bash
grep -rn 'nameof(<Type>' src/                        # persisted? ask what reads that string
grep -rn --include='*.resx' 'Enum_<Type>' src/       # key-forming?
```

Use a bare prefix, not a trailing `*` (`Store*` in grep means "Stor" plus any number of "e"). Never
backfill old rows to match a new name; they were never wrong
([`no-data-backfills`](../process/no-data-backfills.md)).

**Related:** [`no-rename-serialized-fields`](no-rename-serialized-fields.md) covers property names
inside stored JSON.
