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

Also implements the public `ITaskWrite` (bulk load), whose tasks and links save in one transaction through `ITaskRepository.AddManyAsync`.

Claim, release, withdraw, approve and decline write `task_assignments` through `AddAssignmentAsync`, `RemoveAssignmentAsync`, `ConfirmRequestAsync` and `RemoveRequestAsync` (the last two touch only a Requested row).

Cross-section calls via `ITeamService` (team names, slugs, flags), `ISettingsService` (events, gate day),
`IAuditLogService`, `IEmailService` (the approved and declined emails, built by `TasksEmails`) and `IUserService` (address, language). Also the section's `IUserDataContributor` and `IUserMerge`.

### Section (ISectionThingsToDo)

No repository. Reads `ITaskService.GetTasksAsync` and `IPermissionService.GetOwnersAsync` to build the Home entries for waiting requests.

### TasksDemoStory (Scoped, non-production only)

No repository. Writes through `ITaskService` as the admin who started the run; the waiting requests are made as the demo people themselves.
