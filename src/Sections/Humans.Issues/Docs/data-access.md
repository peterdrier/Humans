# Issues — Data Access

## Issues

Project: `src/Sections/Humans.Issues` — services under `Services/`,
repository under `Data/`. **DbContext:** `IssuesDbContext`.
`IssuesRepository` injects `IDbContextFactory<IssuesDbContext>` directly.
Owns `Issues`, `IssueComments`.

### IssuesService (Scoped)

Repository: `IIssuesRepository`.

| Table | R/W |
|-------|-----|
| Issues | R/W |
| IssueComments | R/W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NavBadge:Issues:{userId}` (`IIssuesBadgeCacheInvalidator`) | 2 min | yes | yes | yes |
| `FeedbackBadgeCount` (`INavBadgeCacheInvalidator`) | 2 min | | | yes |

Cross-section calls via `IUserServiceRead`, `IUserEmailService`,
`IRoleAssignmentService`, `IEmailService`,
`IEmailMessageFactory`, `INotificationEmitter`, `INotificationAutoResolve`,
`IAuditLogService`, `IHostEnvironment`. Implements `IUserDataContributor`,
`IIssueTriage` (Backdoor's machine-API triage surface,
nobodies-collective/Humans#1128).

---


