---
name: service-read-no-ef
description: Cross-section read interfaces (I*Read) must expose DTO/Info projections only — no EF entities, no Microsoft.EntityFrameworkCore types, no IQueryable. Structural for entities and EF types; convention for IQueryable.
metadata:
  type: project
---

# I*Read interfaces are DTO-only

Method signatures on any interface whose name ends with `Read` must not reference, at any depth of generic nesting or array element:

- Anything under `Humans.Domain.Entities.*`
- Anything under `Microsoft.EntityFrameworkCore.*` (`DbSet<>`, `EntityEntry`, change-tracking types)
- `System.Linq.IQueryable` / `IQueryable<T>`

Allowed: primitives, `Guid`, `DateTime*`/NodaTime types, enums (including `Humans.Domain.Enums.*`), value objects, project DTOs (`Humans.Application.DTOs.*`), and collection/task wrappers around any of the above.

**Why:** External sections shouldn't depend on another section's storage shape — that couples them to the owning section's EF model and defeats nav-strip / projection work. If a cross-section caller needs entity-shaped data, the section's projection is missing a field; fix the projection, don't widen the read interface. Operationalises [[section-read-write-split]].

**How to apply:**
- **Structural, not analyzer-enforced.** A read interface now lives on a `Humans.<Section>.Contracts` leaf, which references neither the section project nor EF Core, so entity types and `Microsoft.EntityFrameworkCore` types are unnameable there by construction.
- HUM0029 (`ServiceReadInterfaceDtoOnlyAnalyzer`) used to enforce this. It gated on the `Humans.Application` assembly, which declares no `Read`-suffixed interface any more, so it analyzed zero types; retired rather than left as green decoration.
- **`IQueryable<T>` is not covered by the structural boundary** — it is a BCL type any Contracts project can name — so that clause is a convention with nothing enforcing it; tracked by nobodies-collective/Humans#1040.

See also: [[section-read-write-split]], [[no-cross-section-ef-joins]], `docs/architecture/code-analysis.md`.
