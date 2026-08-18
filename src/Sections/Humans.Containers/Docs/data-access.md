# Containers — Data Access

## Containers

Folder: `src/Sections/Humans.Containers/Services/`. **DbContext:**
`ContainersDbContext`. `Repository`
(`src/Sections/Humans.Containers/Data/Repository.cs`, implements
`IContainerRepository`) injects `IDbContextFactory<ContainersDbContext>`
directly. Owns `Containers`, `ContainerPlacements`.

### ContainerService (Scoped)

Repository: `IContainerRepository`.

| Table | R/W |
|-------|-----|
| Containers | R/W |
| ContainerPlacements | R/W |

Cross-section calls via `ICampService`, `IAuditLogService`,
`IFileStorage`. No cache.

---


