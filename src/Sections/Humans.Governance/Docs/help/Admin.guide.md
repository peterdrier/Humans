## Admin Tools

The Admin section provides system-wide management tools for administrators.

### System Operations

- **Configuration Status** — view current system configuration and environment settings
- **Sync Settings** — control Google sync behaviour per service (None / Add Only / Add and Remove)
- **Email Outbox** — view and manage outbound emails
- **Background Jobs** — monitor Hangfire job status and history

### Human Management

- **All Humans** — browse and search all registered humans
- **Role Assignments** — manage governance and system role assignments with temporal tracking
- **Legal Documents** — manage consent documents and their versions

### Google Integration

- **System Team Sync** — runs hourly, syncs team memberships to Google Groups and Shared Drives
- **Reconciliation** — runs daily at 03:00, detects drift between expected and actual Google permissions

### What Happens Automatically

- Hourly sync keeps Google Groups and Drive permissions in sync with team memberships
- Daily reconciliation reports any permission drift for manual review
- Email notifications are sent for role assignment changes
