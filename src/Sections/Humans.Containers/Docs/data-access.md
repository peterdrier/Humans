# Containers — Data Access

## Containers

Folder: `src/Sections/Humans.Containers/Services/`. **DbContext:**
`ContainersDbContext`. `Repository`
(`src/Sections/Humans.Containers/Data/Repository.cs`, implements
`IContainerRepository`) injects `IDbContextFactory<ContainersDbContext>`
directly. Owns `Containers`, `ContainerImages`, `ContainerPlacements`.

### Service (Scoped, registered as `IContainerService`)

Repository: `IContainerRepository`.

| Table | R/W |
|-------|-----|
| Containers | R/W |
| ContainerImages | R/W |
| ContainerPlacements | R/W |

Cross-section calls via `ICampServiceRead`, `IAuditLogService`,
`IFileStorage`. No cache.

---


