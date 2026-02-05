# Background Jobs

## Business Context

Several system operations need to run automatically without user interaction: syncing legal documents from GitHub, sending compliance reminders, enforcing membership rules, and maintaining system team membership. Hangfire provides reliable job scheduling and execution.

## Job Overview

| Job | Schedule | Purpose |
|-----|----------|---------|
| SyncLegalDocumentsJob | Hourly | Sync docs from GitHub |
| SendReConsentReminderJob | Daily | Remind about missing consents |
| SuspendNonCompliantMembersJob | Daily | Enforce compliance deadlines |
| SystemTeamSyncJob | Hourly | Sync system team membership |
| GoogleResourceProvisionJob | On demand | Provision/sync Google resources |

## Job Details

### SyncLegalDocumentsJob

**Purpose**: Keep legal documents synchronized with the canonical GitHub repository.

**Schedule**: Daily at 4:00 AM

**Process**:
```
1. Connect to GitHub API
2. For each configured document path:
   a. Fetch current commit SHA
   b. Compare with stored SHA
   c. If different:
      - Parse document content (ES/EN)
      - Create new DocumentVersion
      - Update LegalDocument.CurrentCommitSha
3. If any documents were updated:
   a. Identify all active users missing new required consents
   b. Send ONE consolidated "Action Required" email per user
   c. Log summary of updates and notifications
```

**Triggers**:
- New document versions requiring re-consent
- Email notifications to affected members (consolidated)
- Status changes for non-compliant members

**Error Handling**:
- GitHub API failures: Retry with backoff
- Parse failures: Log and skip, alert admin
- Partial sync: Continue with remaining docs
- N+1 Protection: Users loaded in batches for notification loop

---

### SendReConsentReminderJob

**Purpose**: Notify members who have missing required consents before enforcement deadlines.

**Schedule**: Daily at 6:00 AM

**Process**:
```
1. Get all users with active roles
2. For each user:
   a. Check for missing required consents
   b. If missing and reminder not sent recently:
      - Calculate days until deadline
      - Select appropriate email template
      - Queue email notification
      - Update last reminder timestamp
3. Log reminder summary
```

**Reminder Timeline**:
```
Day 0:  Document updated (or user becomes active)
Day 1:  First reminder: "Action required"
Day 7:  Second reminder: "One week remaining"
Day 14: Final warning: "Urgent action needed"
Day 30: Suspension (handled by SuspendJob)
```

**Email Content**:
- List of documents needing consent
- Direct links to consent pages
- Deadline date
- Consequences of non-compliance

---

### SuspendNonCompliantMembersJob

**Purpose**: Automatically set members to Inactive status and revoke access when they exceed the consent grace period.

**Schedule**: Daily at 4:30 AM

**Process**:
```
1. Get all users with:
   - Active role assignments
   - Missing required consents
   - Grace period exceeded (e.g. >7 days since update)
2. For each user:
   a. Send suspension notice email
   b. Explicitly revoke access to all Google Drive folders and Groups
   c. Log action with reason
3. Generate compliance report
```

**Safeguards**:
- Only affects users with active roles (not already None)
- Never automatically sets IsSuspended (admin-only)
- Logs all actions for audit
- Access automatically restored by ConsentController when signed

---

### SystemTeamSyncJob

**Purpose**: Maintain automatic membership for the three system teams based on eligibility criteria.

**Schedule**: Every hour

**Process**:
```
1. SyncVolunteersTeamAsync()
   - Eligible: All users with all required consents
   - Add: New eligible users
   - Remove: Users who lost eligibility

2. SyncMetaleadsTeamAsync()
   - Eligible: Users who are Metalead of any user-created team
   - Add: New metaleads
   - Remove: Users who are no longer metalead anywhere

3. SyncBoardTeamAsync()
   - Eligible: Users with active "Board" RoleAssignment
   - Add: New board members
   - Remove: Users whose assignment expired

4. For each membership change:
   - Update Google resource permissions
   - Log change for audit
```

**System Teams**:
| Team | Eligibility Criteria |
|------|---------------------|
| Volunteers | HasAllRequiredConsents = true |
| Metaleads | TeamMember.Role = Metalead (non-system teams) |
| Board | RoleAssignment.RoleName = "Board" AND active |

---

### GoogleResourceProvisionJob

**Purpose**: Ensure all teams have provisioned Google resources and permissions are in sync.

**Schedule**: On demand (or daily if configured)

**Process**:
```
1. Find teams without GoogleResource records
2. For each:
   - Call ProvisionTeamFolderAsync()
   - Create GoogleResource record

3. For all existing resources:
   - Get current team members
   - Get current Google permissions
   - Reconcile differences:
     - Add missing permissions
     - Remove stale permissions
   - Update LastSyncedAt

4. Report any errors
```

## Hangfire Configuration

### Registration (Program.cs)
```csharp
// Configure Hangfire
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options =>
        options.UseNpgsqlConnection(connectionString)));

builder.Services.AddHangfireServer();

// Register jobs
builder.Services.AddScoped<SystemTeamSyncJob>();
builder.Services.AddScoped<SyncLegalDocumentsJob>();
// ... etc

// Schedule recurring jobs
RecurringJob.AddOrUpdate<SystemTeamSyncJob>(
    "system-team-sync",
    job => job.ExecuteAsync(CancellationToken.None),
    Cron.Hourly);
```

### Dashboard
- URL: `/hangfire`
- Authorization: Admin role required (production)
- Features: Job status, retry failed jobs, trigger manual runs

## Job Implementation Pattern

All jobs follow this pattern:
```csharp
public class ExampleJob
{
    private readonly ILogger<ExampleJob> _logger;
    private readonly IClock _clock;
    // ... dependencies

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting job at {Time}",
            _clock.GetCurrentInstant());

        try
        {
            // Job logic here
            await DoWorkAsync(ct);

            _logger.LogInformation("Completed job successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in job");
            throw; // Let Hangfire handle retry
        }
    }
}
```

## Error Handling & Retries

### Hangfire Automatic Retries
- Default: 10 retries with exponential backoff
- Visible in dashboard with error details
- Failed jobs moved to "Failed" queue after all retries

### Custom Retry Logic
```csharp
[AutomaticRetry(Attempts = 3)]
public async Task ExecuteAsync(...)
{
    // Job with custom retry count
}
```

### Error Notification
- Critical job failures logged to Serilog
- Can be routed to Slack/email via Serilog sinks
- Dashboard shows failed job details

## Monitoring

### Metrics (via OpenTelemetry)
- `hangfire_jobs_processed_total` - Counter by job type
- `hangfire_jobs_failed_total` - Counter by job type
- `hangfire_job_duration_seconds` - Histogram

### Health Check
```csharp
builder.Services.AddHealthChecks()
    .AddHangfire(options =>
        options.MinimumAvailableServers = 1,
        name: "hangfire");
```

### Alerts
- No Hangfire server available
- Job failure rate > threshold
- Queue backlog growing

## Testing Jobs

### Unit Testing
```csharp
[Fact]
public async Task SyncJob_ShouldAddNewMembers()
{
    // Arrange: Mock dependencies
    var dbContext = CreateTestDbContext();
    var job = new SystemTeamSyncJob(dbContext, ...);

    // Act
    await job.SyncVolunteersTeamAsync();

    // Assert
    var team = await dbContext.Teams
        .Include(t => t.Members)
        .FirstAsync(t => t.SystemTeamType == SystemTeamType.Volunteers);
    Assert.Contains(team.Members, m => m.UserId == expectedUserId);
}
```

### Manual Trigger
Via Hangfire dashboard or:
```csharp
BackgroundJob.Enqueue<SystemTeamSyncJob>(
    job => job.ExecuteAsync(CancellationToken.None));
```

## Related Features

- [Legal Documents & Consent](04-legal-documents-consent.md) - Document sync job
- [Membership Status](05-membership-status.md) - Compliance jobs
- [Teams](06-teams.md) - System team sync
- [Google Integration](07-google-integration.md) - Resource provisioning job
