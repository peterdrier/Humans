---
name: service-read-no-ef
description: Cross-section read interfaces (I*Read) must expose DTO/Info projections only — no EF entities, no Microsoft.EntityFrameworkCore types, no IQueryable. Analyzer-enforced (HUM0029).
metadata:
  type: project
---

# I*Read interfaces are DTO-only

Method signatures on any interface in `Humans.Application` whose name ends with `Read` must not reference, at any depth of generic nesting or array element:

- Anything under `Humans.Domain.Entities.*`
- Anything under `Microsoft.EntityFrameworkCore.*` (`DbSet<>`, `EntityEntry`, change-tracking types)
- `System.Linq.IQueryable` / `IQueryable<T>`

Allowed: primitives, `Guid`, `DateTime*`/NodaTime types, enums (including `Humans.Domain.Enums.*`), value objects, project DTOs (`Humans.Application.DTOs.*`), and collection/task wrappers around any of the above.

**Why:** External sections shouldn't depend on another section's storage shape — that couples them to the owning section's EF model and defeats nav-strip / projection work. If a cross-section caller needs entity-shaped data, the section's projection is missing a field; fix the projection, don't widen the read interface. Operationalises [[section-read-write-split]].

**How to apply:**
- Enforced by **HUM0029** (`ServiceReadInterfaceDtoOnlyAnalyzer`). Fires at the method declaration that exposes the EF type.
- Grandfather an existing leak with `[Grandfathered("HUM0029", justification, since, issueRef)]` on the interface — downgrades to a Warning so the build still passes while the projection gap is opened as tech debt. See [[analyzer-exceptions-via-attributes]].
- Runs in the `Humans.Application` assembly only — the read interfaces all live there.

See also: [[section-read-write-split]], [[no-cross-section-ef-joins]], `docs/architecture/code-analysis.md`.
