# Tasks — Data Access

## Tasks

Folder: `src/Sections/Humans.Tasks/Services/`, repository under `Data/`.
**DbContext:** `TasksDbContext`. `TaskRepository` injects `IDbContextFactory<TasksDbContext>` directly.
Owns `task_items`, `task_assignments`, `task_dependencies`.

### TaskService (Scoped)

Repository: `ITaskRepository`. No caching decorator.

| Table | R/W |
|-------|-----|
| task_items | R/W |
| task_assignments | R/W |
| task_dependencies | R/W |

Cross-section calls via `ITeamService` (team names, slugs, flags), `ISettingsService` (events, gate day),
`IAuditLogService`. Also the section's `IUserDataContributor` and `IUserMerge`.

### TasksDemoStory (Scoped, non-production only)

No repository. Writes through `ITaskService` as the admin who started the run.
