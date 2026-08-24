<!-- freshness:triggers
  src/Humans.Web/Extensions/RecurringJobExtensions.cs
  src/Sections/*/SectionJobs.cs
  src/Sections/*/Jobs/*Job.cs
  src/Humans.Base/Interfaces/ISectionJobs.cs
-->
<!-- freshness:flag-on-change
  Job catalog, schedules, sync-mode gating, and per-job process descriptions — review whenever a job is added, removed, renamed, or has its schedule/behavior changed.
-->

# Background Jobs

## Business Context

Several system operations need to run automatically without user interaction: syncing legal documents from GitHub, sending compliance reminders, enforcing membership rules, and maintaining system team membership. Hangfire provides reliable job scheduling and execution.

## Job Overview

| Job | Schedule | Purpose |
|-----|----------|---------|
| SyncLegalDocumentsJob | Daily 4 AM | Sync docs from GitHub |
| SendReConsentReminderJob | Daily | Remind about missing consents |
| SuspendNonCompliantMembersJob | Daily 4:30 AM | Enforce compliance deadlines |
| ProcessAccountDeletionsJob | Daily | Process account deletion requests |
| TermRenewalReminderJob | Weekly (Mon 5:00 AM) | Notify humans with approaching Colaborador/Asociado term expiry |
| ProcessEmailOutboxJob | Frequent | Send emails queued in the outbox table |
| CleanupEmailOutboxJob | Weekly (Sun 3:00 AM) | Delete old processed outbox entries |
| ProcessGoogleSyncOutboxJob | Frequent | Process Google sync outbox (add/remove from Groups and Drive) |
| TicketSyncJob | Every 15 min (configurable) | Sync ticket orders from TicketTailor |
| TicketingBudgetSyncJob | Daily 4:30 AM | Materialize weekly ticket actuals into budget line items |
| SendSurveyReminderJob | Daily 9:00 AM | Send 7-day reminder to survey invitees who haven't responded |
| CleanupNotificationsJob | Daily | Delete resolved (>7 days), stale informational (>30 days), and retired-source unresolved notifications |
| CleanupIssuesJob | Daily 5:00 AM | Purge issues that entered a terminal state (Resolved/WontFix/Duplicate) at least 6 months ago, plus their screenshot directories |
| SystemTeamSyncJob | Hourly | Sync system team membership + Google permissions |
| GoogleResourceReconciliationJob | Daily 3:00 AM | Full Google resource reconciliation |
| DriveActivityMonitorJob | Hourly | Check Drive Activity API for anomalous permission changes |
| HoldedExpenseOutboxJob | Every minute | Drain the Holded expense outbox: push approved expense reports to Holded as purchase documents |
| HoldedSyncJob | Daily 3:00 AM | Nightly pull of Holded purchase docs into budget-category actuals, plus a trailing 364-day sweep of the creditor daybook ledger (full-history backfill only on a cold cache or via the on-demand `POST /Holded/FullSync`) |
| GateRetentionJob | Daily 3:45 AM | Purge `gate_scan_events` older than `Gate:RetentionDays` (default 365; ≤ 0 disables the purge) |
| GateVendorCheckInJob | On demand (enqueued) | Best-effort mirror of a gate admit to the ticket vendor (TicketTailor check-in); fire-and-forget from the gate controller, no retries (vendor check-ins aren't idempotent), gated by `Gate:VendorMirrorEnabled` (default off) |
| AgentConversationRetentionJob | Daily 3:15 AM | Purge agent conversations past the retention window |
| MailerLiteAudienceSyncJob | Opt-in; no default schedule | Sync all MailerLite audiences. Registered only when `MailerLite:AudienceSyncCron` is set to a cron expression — the setting ships empty, so by default this job does not run at all and syncing is on-demand via the `/MailerLite/Admin` "Push Now" button |

> **Note:** `SystemTeamSyncJob` and `GoogleResourceReconciliationJob` were historically disabled by default because they modify Google Shared Drive and Group permissions; both are now registered as normal scheduled jobs (`teams-system-sync` hourly, `google-resource-reconciliation` daily at 03:00) in `RecurringJobExtensions.UseHumansRecurringJobs`. `GoogleResourceReconciliationJob` still no-ops per service when that service's sync mode is `None` (configured at `/Google/SyncSettings`). The manual "Sync Now" button at `/Google/Sync` remains available for on-demand runs. `SendAdminDailyDigestJob` / `SendBoardDailyDigestJob` have been retired and their job types no longer exist; deleting a job needs no cleanup step, because startup removes every stored Hangfire schedule that is not in the roll-call.

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

**Schedule**: Daily at 4:00 AM (30 minutes before `SuspendNonCompliantMembersJob`)

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

### ProcessAccountDeletionsJob

**Purpose**: Anonymize accounts where the 30-day GDPR deletion grace period has expired.

**Schedule**: Daily

**Process**:
```
1. Find users where DeletionScheduledFor <= now
2. For each user:
   a. Anonymize user record (display name, email, phone, pronouns, DOB, profile picture, emergency contacts)
   b. Remove related data (UserEmails, ContactFields, VolunteerHistoryEntries)
   c. End all team memberships (LeftAt = now)
   d. End active role assignments (ValidTo = now)
   e. Disable login (LockoutEnd = MaxValue, rotate SecurityStamp)
   f. Clear DeletionRequestedAt / DeletionScheduledFor
   g. Audit log: AccountAnonymized
   h. Send confirmation email to original address
3. SaveChanges (single transaction for all users)
```

**Google deprovisioning**: Not handled by this job. Team membership endings (step 2c) are picked up by the normal sync jobs (`SystemTeamSyncJob` / `GoogleResourceReconciliationJob`), which remove the corresponding Google Group memberships and Shared Drive permissions. This keeps deprovisioning on the same code path as any other team departure.

**Preserved for audit trail**: ConsentRecords and Applications are kept (anonymized implicitly via the user record). ConsentRecords are immutable (DB triggers prevent UPDATE/DELETE).

See [Profiles — Account Deletion](../../../src/Sections/Humans.Users/Docs/features/profiles.md#account-deletion-right-to-erasure) for the full user-facing workflow.

---

### SystemTeamSyncJob

**Purpose**: Maintain automatic membership for the three system teams based on eligibility criteria. Also syncs Google Shared Drive and Group permissions for each membership change.

**Schedule**: Hourly (Hangfire recurring job `teams-system-sync`). Can also be triggered manually from the Admin dashboard via "Sync System Teams" button.

**Inline Triggers**: After the name-only access switch, the consent-write and CC-clear paths no longer fire a per-user Volunteers sync — admission is reconciled by the scheduled `SyncVolunteersTeamAsync` pass (eventually consistent), and access never depended on Volunteers membership. `SyncVolunteersMembershipForUserAsync(userId)` (single-user, no effect on other members) remains available and is still triggered by per-user lifecycle events (e.g. role/lead changes via the other system-team paths).

**Process**:
```
1. SyncVolunteersTeamAsync()
   - Eligible: HasRequiredNameFields (legal name entered), !IsSuspended, RejectedAt is null, with all required Volunteers-team consents — i.e. NAME + CONSENTS
   - Add: New eligible users
   - Remove: Users who lost eligibility (suspended, rejected, or consent lapsed)
   - (Profile.ConsentCheckStatus and Profile.IsApproved are CC audit annotations; not consulted)

2. SyncCoordinatorsTeamAsync()
   - Eligible: Users who are Coordinator of any user-created team + Coordinators-team consents
   - Add: New coordinators
   - Remove: Users who are no longer coordinator anywhere

3. SyncBoardTeamAsync()
   - Eligible: Users with active "Board" RoleAssignment + Board-team consents
   - Add: New board members
   - Remove: Users whose assignment expired

4. For each membership change:
   - Update Google resource permissions
   - Log change for audit
```

**System Teams**:
| Team | Eligibility Criteria |
|------|---------------------|
| Volunteers | HasRequiredNameFields AND !IsSuspended AND RejectedAt is null AND HasAllRequiredConsentsForTeam(Volunteers) — name + consents (ConsentCheckStatus/IsApproved not consulted) |
| Coordinators | TeamMember.Role = Coordinator (non-system teams) AND HasAllRequiredConsentsForTeam(Coordinators) |
| Board | RoleAssignment.RoleName = "Board" AND active AND HasAllRequiredConsentsForTeam(Board) |

**Single-User Sync**: `SyncVolunteersMembershipForUserAsync(userId)` evaluates one user against the Volunteers team criteria and adds or removes them without affecting other members. After the name-only access switch the consent-submit and CC-clear paths no longer call it, so Volunteers admission is reconciled by the scheduled `SyncVolunteersTeamAsync` pass (eventually consistent); app access never depended on Volunteers membership. The per-user method remains available for lifecycle events that route through `SyncMembershipForUserAsync`.

---

### GoogleResourceReconciliationJob

**Purpose**: Full reconciliation of all Google resources (Shared Drive folders + Groups) with the expected state from the database. Reads per-service sync mode from the `sync_service_settings` table to determine what actions to take.

**Schedule**: Daily at 3:00 AM (Hangfire recurring job `google-resource-reconciliation`)

**Process**:
```
1. For each service type (GoogleDrive, GoogleGroups):
   a. Read SyncMode from sync_service_settings
   b. If mode is None → skip this service entirely
   c. Map SyncMode to SyncAction:
      - AddOnly → SyncAction.AddOnly (adds only)
      - AddAndRemove → SyncAction.AddAndRemove (adds + removes)
   d. Call SyncResourcesByTypeAsync(resourceType, action)
      - Computes diff for all active resources of that type
      - Executes adds (and removes if AddAndRemove) per resource
      - Per-resource error handling (log + store ErrorMessage, continue)
      - Updates LastSyncedAt on each resource
```

**Sync modes** (configured at `/Google/SyncSettings` by Admin users):
| Mode | Behavior |
|------|----------|
| `None` | Job skips this service entirely |
| `AddOnly` | Only adds missing members — never removes |
| `AddAndRemove` | Full sync: adds missing + removes extra members |

**Group settings drift check + auto-remediation**: After membership reconciliation, the job checks all active Google Groups for settings drift (e.g., someone changed WhoCanPost in Google Admin). When drift is found, settings are automatically reapplied via `RemediateGroupSettingsAsync` and each remediation is audit-logged. Per-group remediation failures are logged but do not abort the check phase. Respects the GoogleGroups sync mode: skipped entirely if set to None.

> Runs on its normal daily schedule; use manual "Sync Now" at `/Google/Sync` for an immediate run, or configure per-service sync modes at `/Google/SyncSettings`.

## Hangfire Configuration

### Registration (Program.cs)

`Program.cs` only configures the Hangfire server itself — storage and the dashboard — and calls `app.UseHumansRecurringJobs()` at startup:

```csharp
builder.Services.AddHangfire((sp, config) => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options =>
        options.UseNpgsqlConnection(connectionString)));

builder.Services.AddHangfireServer();
// ...
app.UseHumansRecurringJobs();
```

There is no central roll-call and no `AddScoped<...Job>()` list in `Program.cs`. Each section implements `ISectionJobs.Jobs(IServiceProvider)`, yielding a `RecurringJobDescriptor(Id, JobType, Cron)` per job it owns — job types are DI-registered by that section's own `Section.cs`, not by Shell. `RecurringJobExtensions.ContributedJobs` discovers every `ISectionJobs` implementation and flattens their descriptors into the one set Shell schedules and sweeps:

```csharp
// src/Sections/Humans.Consent/SectionJobs.cs
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "consent-reconsent-reminders", typeof(SendReConsentReminderJob), "0 4 * * *");
        yield return new RecurringJobDescriptor(
            "consent-legal-document-sync", typeof(SyncLegalDocumentsJob), "0 4 * * *");
    }
}
```

Job ids are section-first (`consent-legal-document-sync`, `issues-cleanup`, `tickets-vendor-sync`) so the owning section is obvious from the id alone. Ids are stored in Hangfire and must never change — a rename is a new job plus a swept-away old one.

Startup schedules every contributed job and then deletes any stored Hangfire schedule whose id is not in the contributed set (skipped if any job failed to register, since a stale entry might be the only working copy of a schedule that couldn't be rewritten). Renaming or deleting a job therefore needs nothing beyond editing its section's `SectionJobs.Jobs` — the old entry goes away on the next boot, taking its dashboard history with it. An opt-in job (empty `Cron`) keeps its place in the contributed set even when its schedule is switched off, so turning one off never gets it swept away; `RecurringJob.RemoveIfExists` drops any stored schedule left from when it was on.

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

- [Legal Documents & Consent](../../../src/Sections/Humans.Consent/Docs/features/legal-documents-consent.md) - Document sync job
- [Volunteer Status](../../../src/Sections/Humans.Onboarding/Docs/features/volunteer-status.md) - Compliance jobs
- [Teams](../../../src/Sections/Humans.Teams/Docs/features/Teams-feature.md) - System team sync
- [Google Integration](../../../src/Sections/Humans.GoogleIntegration/Docs/features/google-integration.md) - Resource provisioning job
- [Drive Activity Monitoring](../../../src/Sections/Humans.GoogleIntegration/Docs/features/drive-activity-monitoring.md) - Anomalous permission detection
