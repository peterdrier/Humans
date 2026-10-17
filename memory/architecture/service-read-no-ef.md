---
name: service-read-no-ef
description: Public Contracts (`I<Section>Service`, `I<Section>Write`) expose DTO/Info projections only — no EF entities, no `Microsoft.EntityFrameworkCore` types, no `IQueryable`.
metadata:
  type: project
---

# Public contracts are DTO-only

Method signatures on any public contract interface (`Contracts/` folder or `.Contracts` leaf) must not reference, at any depth of generic nesting or array element:

- Anything under `Humans.Domain.Entities.*`
- Anything under `Microsoft.EntityFrameworkCore.*` (`DbSet<>`, `EntityEntry`, change-tracking types)
- `System.Linq.IQueryable` / `IQueryable<T>`

Allowed: primitives, `Guid`, `DateTime*`/NodaTime types, enums (including `Humans.Domain.Enums.*`), value objects, project DTOs (`Humans.Application.DTOs.*`), and collection/task wrappers around any of the above.

**Why:** External sections shouldn't depend on another section's storage shape — that couples them to the owning section's EF model and defeats nav-strip / projection work. If a cross-section caller needs entity-shaped data, the section's projection is missing a field; fix the projection, don't widen the contract. Operationalises [[section-public-contract]].

**How to apply:**
- **Compile-time enforcement:** `ContractPersistenceRule` (HUM0037) rejects EF infrastructure and `IQueryable` references in every `Contracts/` folder and `.Contracts` leaf, including nested generic signatures and implementation bodies. Derived contexts are also rejected.
- Section entities remain `internal` (HUM0034), so public contracts cannot expose them (CS0050). Contract leaves cannot reference their owning section without creating a project cycle.
- `Humans.Users.Contracts` deliberately declares `User : IdentityUser<Guid>` for ASP.NET Identity's generic surface. Identity user models are allowed; this is not an exemption for EF infrastructure in that leaf.

See also: [[section-public-contract]], [[no-cross-section-ef-joins]], `docs/architecture/code-analysis.md`.
