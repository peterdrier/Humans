# Email Outbox & Campaign System Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace inline SMTP sends with a database outbox for reliable delivery, polish the email template, and add a campaign system for sending individualized codes to members in waves.

**Architecture:** All 15 email methods switch from inline SMTP to writing outbox rows. A Hangfire job processes the outbox with throttling, retry, and crash recovery. Campaign system builds on the outbox to track code→human assignments and delivery. Admin dashboards provide visibility.

**Tech Stack:** .NET 9, EF Core + PostgreSQL, Hangfire, MailKit, NodaTime, xUnit + NSubstitute

**Spec:** `docs/superpowers/specs/2026-03-14-email-outbox-campaigns-design.md`

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `src/Humans.Domain/Entities/EmailOutboxMessage.cs` | Outbox message entity |
| `src/Humans.Domain/Entities/Campaign.cs` | Campaign entity |
| `src/Humans.Domain/Entities/CampaignCode.cs` | Imported code pool entity |
| `src/Humans.Domain/Entities/CampaignGrant.cs` | Code↔human assignment entity |
| `src/Humans.Domain/Enums/EmailOutboxStatus.cs` | Queued, Sent, Failed |
| `src/Humans.Domain/Enums/CampaignStatus.cs` | Draft, Active, Completed |
| `src/Humans.Application/Interfaces/IEmailTransport.cs` | SMTP transport interface |
| `src/Humans.Application/Interfaces/ICampaignService.cs` | Campaign business logic interface |
| `src/Humans.Infrastructure/Services/OutboxEmailService.cs` | IEmailService → outbox writes |
| `src/Humans.Infrastructure/Services/SmtpEmailTransport.cs` | IEmailTransport → MailKit SMTP |
| `src/Humans.Infrastructure/Services/StubEmailTransport.cs` | IEmailTransport → logging only |
| `src/Humans.Infrastructure/Services/CampaignService.cs` | Campaign CRUD, wave send, import |
| `src/Humans.Infrastructure/Jobs/ProcessEmailOutboxJob.cs` | Outbox processor (Hangfire) |
| `src/Humans.Infrastructure/Jobs/CleanupEmailOutboxJob.cs` | Purge old sent messages |
| `src/Humans.Infrastructure/Data/Configurations/EmailOutboxMessageConfiguration.cs` | EF config |
| `src/Humans.Infrastructure/Data/Configurations/CampaignConfiguration.cs` | EF config |
| `src/Humans.Infrastructure/Data/Configurations/CampaignCodeConfiguration.cs` | EF config |
| `src/Humans.Infrastructure/Data/Configurations/CampaignGrantConfiguration.cs` | EF config |
| `src/Humans.Web/Controllers/CampaignController.cs` | Admin campaign pages |
| `src/Humans.Web/Controllers/UnsubscribeController.cs` | Public unsubscribe endpoint |
| `src/Humans.Web/Views/Campaign/Index.cshtml` | Campaign list |
| `src/Humans.Web/Views/Campaign/Create.cshtml` | Create campaign form |
| `src/Humans.Web/Views/Campaign/Detail.cshtml` | Campaign detail + grants |
| `src/Humans.Web/Views/Campaign/SendWave.cshtml` | Wave send form |
| `src/Humans.Web/Views/Admin/EmailOutbox.cshtml` | Outbox dashboard |
| `src/Humans.Web/Views/Unsubscribe/Index.cshtml` | Unsubscribe confirmation |
| `src/Humans.Web/Views/Unsubscribe/Expired.cshtml` | Expired token message |
| `src/Humans.Web/Views/Unsubscribe/Done.cshtml` | Post-unsubscribe confirmation |
| `tests/Humans.Application.Tests/Jobs/ProcessEmailOutboxJobTests.cs` | Processor tests |
| `tests/Humans.Application.Tests/Jobs/CleanupEmailOutboxJobTests.cs` | Cleanup job tests |
| `tests/Humans.Application.Tests/Services/OutboxEmailServiceTests.cs` | Outbox service tests |
| `tests/Humans.Application.Tests/Services/CampaignServiceTests.cs` | Campaign service tests |

### Modified Files

| File | Change |
|------|--------|
| `src/Humans.Domain/Entities/User.cs` | Add `UnsubscribedFromCampaigns` bool |
| `src/Humans.Infrastructure/Data/HumansDbContext.cs` | Add 4 new DbSets |
| `src/Humans.Infrastructure/Configuration/EmailSettings.cs` | Add outbox settings |
| `src/Humans.Application/Interfaces/IHumansMetrics.cs` | Add 3 new methods |
| `src/Humans.Infrastructure/Services/HumansMetricsService.cs` | Implement 3 new metrics |
| `src/Humans.Infrastructure/Services/SmtpEmailService.cs` | Keep as rollback; no changes |
| `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` | Register new services |
| `src/Humans.Web/Extensions/RecurringJobExtensions.cs` | Register 2 new jobs |
| `src/Humans.Web/Views/Shared/_Layout.cshtml` | Add Campaign nav item (admin) |
| `src/Humans.Web/Views/Profile/Index.cshtml` | Add campaign codes section |

---

## Chunk 1: Email Outbox Foundation

### Task 1: Domain Entities & Enums

**Files:**
- Create: `src/Humans.Domain/Enums/EmailOutboxStatus.cs`
- Create: `src/Humans.Domain/Entities/EmailOutboxMessage.cs`

- [ ] **Step 1: Create EmailOutboxStatus enum**

```csharp
// src/Humans.Domain/Enums/EmailOutboxStatus.cs
namespace Humans.Domain.Enums;

public enum EmailOutboxStatus
{
    Queued = 0,
    Sent = 1,
    Failed = 2
}
```

- [ ] **Step 2: Create EmailOutboxMessage entity**

```csharp
// src/Humans.Domain/Entities/EmailOutboxMessage.cs
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class EmailOutboxMessage
{
    public Guid Id { get; set; }
    public string RecipientEmail { get; set; } = string.Empty;
    public string? RecipientName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string? PlainTextBody { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public Guid? CampaignGrantId { get; set; }
    public string? ReplyTo { get; set; }
    public string? ExtraHeaders { get; set; }
    public EmailOutboxStatus Status { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant? SentAt { get; set; }
    public Instant? PickedUpAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public Instant? NextRetryAt { get; set; }

    // Navigation
    public User? User { get; set; }
    public CampaignGrant? CampaignGrant { get; set; }
}
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build src/Humans.Domain/Humans.Domain.csproj`
Expected: Success (CampaignGrant reference will cause a warning but Domain project should still build since it's just a navigation property type that doesn't exist yet — add it in Task 7)

**Note:** The `CampaignGrant` navigation property will cause a build error. For now, comment it out. It will be uncommented in Task 7 when the Campaign entities are created.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Domain/Enums/EmailOutboxStatus.cs src/Humans.Domain/Entities/EmailOutboxMessage.cs
git commit -m "feat(email): add EmailOutboxMessage entity and EmailOutboxStatus enum"
```

### Task 2: EF Configuration & EmailSettings

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/EmailOutboxMessageConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`
- Modify: `src/Humans.Infrastructure/Configuration/EmailSettings.cs`

- [ ] **Step 1: Create EF configuration**

Follow the pattern in `GoogleSyncOutboxEventConfiguration.cs`:

```csharp
// src/Humans.Infrastructure/Data/Configurations/EmailOutboxMessageConfiguration.cs
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class EmailOutboxMessageConfiguration : IEntityTypeConfiguration<EmailOutboxMessage>
{
    public void Configure(EntityTypeBuilder<EmailOutboxMessage> builder)
    {
        builder.ToTable("email_outbox_messages");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.RecipientEmail).HasMaxLength(320).IsRequired();
        builder.Property(e => e.RecipientName).HasMaxLength(200);
        builder.Property(e => e.Subject).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.HtmlBody).IsRequired();
        builder.Property(e => e.TemplateName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.ReplyTo).HasMaxLength(320);
        builder.Property(e => e.ExtraHeaders).HasMaxLength(4000);
        builder.Property(e => e.LastError).HasMaxLength(4000);
        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Processor query index
        builder.HasIndex(e => new { e.SentAt, e.RetryCount, e.NextRetryAt, e.PickedUpAt });
        // User email history
        builder.HasIndex(e => e.UserId);
        // Campaign grant tracking
        builder.HasIndex(e => e.CampaignGrantId);

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
```

- [ ] **Step 2: Add DbSet to HumansDbContext**

Add to `HumansDbContext.cs` alongside other DbSets:

```csharp
public DbSet<EmailOutboxMessage> EmailOutboxMessages { get; set; } = null!;
```

- [ ] **Step 3: Add outbox settings to EmailSettings**

Add these properties to `EmailSettings.cs`:

```csharp
public int OutboxBatchSize { get; set; } = 10;
public int OutboxMaxRetries { get; set; } = 10;
public int OutboxRetentionDays { get; set; } = 150;
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj`

- [ ] **Step 5: Create EF migration**

Run: `dotnet ef migrations add AddEmailOutbox --project src/Humans.Infrastructure --startup-project src/Humans.Web`

Verify the migration creates the `email_outbox_messages` table with correct columns and indexes.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(email): add EmailOutboxMessage EF configuration and migration"
```

### Task 3: IEmailTransport & SmtpEmailTransport

**Files:**
- Create: `src/Humans.Application/Interfaces/IEmailTransport.cs`
- Create: `src/Humans.Infrastructure/Services/SmtpEmailTransport.cs`
- Create: `src/Humans.Infrastructure/Services/StubEmailTransport.cs`

- [ ] **Step 1: Create IEmailTransport interface**

```csharp
// src/Humans.Application/Interfaces/IEmailTransport.cs
namespace Humans.Application.Interfaces;

public interface IEmailTransport
{
    Task SendAsync(string recipientEmail, string? recipientName,
        string subject, string htmlBody, string? plainTextBody,
        string? replyTo = null,
        IDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Create SmtpEmailTransport**

Extract the SMTP connection/send logic from `SmtpEmailService.SendEmailAsync()` (lines 248-342). The new class handles only SMTP transport — no rendering, no template wrapping, no metrics.

Reference `SmtpEmailService.cs` for the MailKit connection pattern:
- Connect to `_settings.SmtpHost:SmtpPort` with `SecureSocketOptions.StartTls`
- Authenticate with `_settings.Username`/`Password`
- Build `MimeMessage` with From, To, Subject, multipart body (HTML + plain text)
- Add ReplyTo header if provided
- Add extra headers from dictionary
- Send and disconnect

```csharp
// src/Humans.Infrastructure/Services/SmtpEmailTransport.cs
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Humans.Infrastructure.Services;

public class SmtpEmailTransport : IEmailTransport
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SmtpEmailTransport> _logger;

    public SmtpEmailTransport(
        IOptions<EmailSettings> settings,
        ILogger<SmtpEmailTransport> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendAsync(string recipientEmail, string? recipientName,
        string subject, string htmlBody, string? plainTextBody,
        string? replyTo = null,
        IDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));
        message.To.Add(new MailboxAddress(recipientName, recipientEmail));
        message.Subject = subject;

        if (!string.IsNullOrEmpty(replyTo))
            message.ReplyTo.Add(MailboxAddress.Parse(replyTo));

        if (extraHeaders != null)
        {
            foreach (var (key, value) in extraHeaders)
                message.Headers.Add(key, value);
        }

        var builder = new BodyBuilder { HtmlBody = htmlBody };
        if (!string.IsNullOrEmpty(plainTextBody))
            builder.TextBody = plainTextBody;
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(_settings.SmtpHost, _settings.SmtpPort,
            _settings.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
            cancellationToken);

        if (!string.IsNullOrEmpty(_settings.Username))
            await client.AuthenticateAsync(_settings.Username, _settings.Password, cancellationToken);

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        _logger.LogInformation("Email sent to {Recipient}: {Subject}", recipientEmail, subject);
    }
}
```

- [ ] **Step 3: Create StubEmailTransport**

```csharp
// src/Humans.Infrastructure/Services/StubEmailTransport.cs
using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services;

public class StubEmailTransport : IEmailTransport
{
    private readonly ILogger<StubEmailTransport> _logger;

    public StubEmailTransport(ILogger<StubEmailTransport> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string recipientEmail, string? recipientName,
        string subject, string htmlBody, string? plainTextBody,
        string? replyTo = null,
        IDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Email to {Recipient}: {Subject}", recipientEmail, subject);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build Humans.slnx`

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(email): add IEmailTransport interface with SMTP and stub implementations"
```

### Task 4: Metrics Extensions

**Files:**
- Modify: `src/Humans.Application/Interfaces/IHumansMetrics.cs`
- Modify: `src/Humans.Infrastructure/Services/HumansMetricsService.cs`

- [ ] **Step 1: Add new methods to IHumansMetrics**

Add to the interface:

```csharp
void RecordEmailQueued(string template);
void RecordEmailFailed(string template);
void SetEmailOutboxPending(int count);
```

- [ ] **Step 2: Implement in HumansMetricsService**

Follow the existing pattern in `HumansMetricsService.cs`. Add counters and gauge using the existing meter. Use dot-delimited names matching existing convention (e.g., `humans.email_queued_total`).

- [ ] **Step 3: Build to verify**

Run: `dotnet build Humans.slnx`

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(email): add email outbox metrics (queued, failed, pending gauge)"
```

### Task 5: OutboxEmailService

This is the core change — all 15 email methods switch from SMTP to outbox writes.

**Files:**
- Create: `src/Humans.Infrastructure/Services/OutboxEmailService.cs`
- Create: `tests/Humans.Application.Tests/Services/OutboxEmailServiceTests.cs`

- [ ] **Step 1: Write tests for OutboxEmailService**

Test that calling an email method:
1. Creates an `EmailOutboxMessage` row in the database
2. Sets correct TemplateName, Status=Queued, RecipientEmail, Subject, HtmlBody
3. Records the `emails_queued` metric
4. For time-sensitive templates (email_verification), also enqueues a Hangfire job via `IBackgroundJobClient` (assert with `_backgroundJobClient.Received()`)

Use the existing test pattern from `ConsentServiceTests.cs` or `ApplicationDecisionServiceTests.cs`:
- In-memory database
- NSubstitute mocks for `IEmailRenderer`, `IHumansMetrics`, `IClock`, `IBackgroundJobClient`
- FakeClock for deterministic timestamps

Test at least 3 representative methods:
- `SendWelcomeEmailAsync` (basic transactional)
- `SendEmailVerificationAsync` (time-sensitive — should trigger immediate processing)
- `SendFacilitatedMessageAsync` (has ReplyTo)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Humans.Application.Tests --filter "OutboxEmailService"`
Expected: FAIL — class doesn't exist yet

- [ ] **Step 3: Implement OutboxEmailService**

Implements `IEmailService`. Constructor injects: `HumansDbContext`, `IEmailRenderer`, `IHumansMetrics`, `IClock`, `IHostEnvironment`, `IOptions<EmailSettings>`, `IBackgroundJobClient`, `ILogger<OutboxEmailService>`.

Each method:
1. Renders via `_renderer.Render*()` to get `EmailContent(subject, htmlBody)`
2. Calls `WrapInTemplate(htmlBody)` — move this method from SmtpEmailService
3. Calls `HtmlToPlainText(wrappedHtml)` — move this too
4. Creates `EmailOutboxMessage` with Status=Queued, CreatedAt=now
5. Adds to DbContext, saves
6. Records `_metrics.RecordEmailQueued(templateName)`
7. For time-sensitive templates: `_backgroundJobClient.Enqueue<ProcessEmailOutboxJob>(x => x.ExecuteAsync(default))` (uses injected `IBackgroundJobClient`, not static `BackgroundJob`)

The `WrapInTemplate` method gets the polished template (Task 6), but implement the basic version first (copy from SmtpEmailService), then polish in Task 6.

**Important:** `SendFacilitatedMessageAsync` must set `ReplyTo` on the outbox message. Check the existing SmtpEmailService implementation for how ReplyTo is currently handled.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Humans.Application.Tests --filter "OutboxEmailService"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(email): add OutboxEmailService — all email sends go through outbox"
```

### Task 6: Email Template Polish

**Files:**
- Modify: `src/Humans.Infrastructure/Services/OutboxEmailService.cs` (the `WrapInTemplate` method)

- [ ] **Step 1: Update WrapInTemplate with polished design**

Replace the basic template (copied from SmtpEmailService in Task 5) with the polished version per the spec:
- Dark header bar (#3d2b1f) with gold "Humans" wordmark
- Gold accent border below header (#c9a96e, 3px)
- Warm parchment footer background (#f0e2c8)
- 24px horizontal padding
- Keep environment banner above header
- All styles inline for Gmail compatibility

Reference the existing `WrapInTemplate` in `SmtpEmailService.cs:344-381` and the mockup from brainstorming.

- [ ] **Step 2: Update email preview to use OutboxEmailService template**

Check if `Views/Admin/EmailPreview.cshtml` renders via `IEmailRenderer` or `IEmailService`. If it uses the renderer directly, it won't show the template wrapper. May need to add a preview helper that wraps content in the template.

- [ ] **Step 3: Build and visually verify**

Run the app and check `/Admin/EmailPreview` to see the new template applied to all email types.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(email): polish email template with branded header and parchment footer"
```

### Task 7: Global Pause (SystemSettings)

**Must come before ProcessEmailOutboxJob — the processor depends on this entity.**

**Files:**
- Create: `src/Humans.Domain/Entities/SystemSetting.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/SystemSettingConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs` (add DbSet)

- [ ] **Step 1: Create SystemSetting entity**

Simple key-value entity:

```csharp
namespace Humans.Domain.Entities;

public class SystemSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Create EF configuration**

Primary key on `Key`. Table: `system_settings`. Seed a row: `Key="IsEmailSendingPaused", Value="false"`.

- [ ] **Step 3: Add DbSet and create migration**

Add `DbSet<SystemSetting>` to `HumansDbContext`. Create migration.

Run: `dotnet ef migrations add AddSystemSettings --project src/Humans.Infrastructure --startup-project src/Humans.Web`

- [ ] **Step 4: Build to verify**

Run: `dotnet build Humans.slnx`

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(email): add SystemSettings entity and global email pause flag"
```

### Task 8: ProcessEmailOutboxJob

**Files:**
- Create: `src/Humans.Infrastructure/Jobs/ProcessEmailOutboxJob.cs`
- Create: `tests/Humans.Application.Tests/Jobs/ProcessEmailOutboxJobTests.cs`

- [ ] **Step 1: Write tests for ProcessEmailOutboxJob**

Follow `ProcessGoogleSyncOutboxJobTests.cs` pattern. **Note:** Unlike the Google sync test, this job requires `IOptions<EmailSettings>` — construct via `Options.Create(new EmailSettings { OutboxBatchSize = 10, OutboxMaxRetries = 10 })`.

Test cases:

1. **Processes queued messages** — seed a Queued message, run job, verify transport was called, status=Sent, SentAt set
2. **Handles failure** — mock transport to throw, verify status=Failed, RetryCount=1, LastError set, NextRetryAt set
3. **Respects batch size** — seed 15 messages, verify only 10 processed (default batch)
4. **Skips paused** — seed `IsEmailSendingPaused=true` in SystemSettings, seed messages, verify none processed
5. **Crash recovery** — seed message with PickedUpAt 6 minutes ago and SentAt=null, verify it's re-picked
6. **Skips recently picked up** — seed message with PickedUpAt 2 minutes ago, verify it's skipped
7. **Exponential backoff** — seed Failed message with RetryCount=3, NextRetryAt in the past, verify it's retried
8. **Skips future retry** — seed Failed message with NextRetryAt in the future, verify it's skipped

**Note:** Test case for "Updates campaign grant status" is deferred to after Task 13 (when CampaignGrant entity exists). Add it then.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Humans.Application.Tests --filter "ProcessEmailOutboxJob"`
Expected: FAIL

- [ ] **Step 3: Implement ProcessEmailOutboxJob**

Follow the spec's processor algorithm. Constructor injects: `HumansDbContext`, `IEmailTransport`, `HumansMetricsService`, `IClock`, `IOptions<EmailSettings>`, `ILogger<ProcessEmailOutboxJob>`.

Key implementation details:
- Global pause: query `SystemSettings` DbSet for `IsEmailSendingPaused` key. If `"true"`, log and return.
- SMTP connection: call `IEmailTransport.SendAsync` per message. Connection reuse is deferred — at 10 msgs/min the overhead is negligible. Can be optimized later by adding a batch-aware method to `IEmailTransport`.
- ExtraHeaders: deserialize from JSON string to `Dictionary<string, string>` using `System.Text.Json`.
- On successful send: call `_metrics.RecordEmailSent(message.TemplateName)` (reuse existing metric) in addition to updating status.
- Campaign grant update: if `CampaignGrantId` is set, load the grant and update `LatestEmailStatus` and `LatestEmailAt`. (Grant entity won't exist until Chunk 2 — guard with a null check on the navigation property, or defer this logic to Task 13.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Humans.Application.Tests --filter "ProcessEmailOutboxJob"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(email): add ProcessEmailOutboxJob — outbox processor with retry and crash recovery"
```

### Task 9: CleanupEmailOutboxJob

**Files:**
- Create: `src/Humans.Infrastructure/Jobs/CleanupEmailOutboxJob.cs`
- Create: `tests/Humans.Application.Tests/Jobs/CleanupEmailOutboxJobTests.cs`

- [ ] **Step 1: Write tests**

1. Deletes sent messages older than retention period
2. Keeps sent messages within retention period
3. Keeps failed messages regardless of age
4. Keeps queued messages regardless of age

- [ ] **Step 2: Run tests to verify they fail**

- [ ] **Step 3: Implement CleanupEmailOutboxJob**

Simple job: `DELETE FROM email_outbox_messages WHERE Status = 'Sent' AND SentAt < now - RetentionDays`

- [ ] **Step 4: Run tests to verify they pass**

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(email): add CleanupEmailOutboxJob — purge sent messages after retention period"
```

### Task 10: DI Registration & Job Scheduling

**Files:**
- Modify: `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`
- Modify: `src/Humans.Web/Extensions/RecurringJobExtensions.cs`

- [ ] **Step 1: Update DI registration**

In `InfrastructureServiceCollectionExtensions.cs`:
- Change `IEmailService` registration from `SmtpEmailService` to `OutboxEmailService`
- Add `IEmailTransport` registration with environment-based switching (SmtpEmailTransport vs StubEmailTransport)
- Add `ICampaignService` → `CampaignService` (will be created in Chunk 2, can skip for now)
- Keep `SmtpEmailService` registered as itself for rollback (or just leave the class — don't delete it)

Follow the existing stub pattern used for Google services.

- [ ] **Step 2: Register Hangfire jobs**

In `RecurringJobExtensions.cs`, add:

```csharp
RecurringJob.AddOrUpdate<ProcessEmailOutboxJob>(
    "process-email-outbox",
    job => job.ExecuteAsync(CancellationToken.None),
    "*/1 * * * *"); // Every minute

RecurringJob.AddOrUpdate<CleanupEmailOutboxJob>(
    "cleanup-email-outbox",
    job => job.ExecuteAsync(CancellationToken.None),
    "0 3 * * 0"); // Sunday 03:00 UTC
```

- [ ] **Step 3: Build the full solution**

Run: `dotnet build Humans.slnx`

- [ ] **Step 4: Run all tests**

Run: `dotnet test Humans.slnx`
Expected: All pass. Existing email tests may need updating if they mock `IEmailService` — verify.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(email): register outbox services and Hangfire jobs in DI"
```

### Task 11: Integration Test — Outbox End-to-End

- [ ] **Step 1: Manual QA verification**

Start the app locally (or in QA). Trigger an email (e.g., email verification from profile). Verify:
1. Email appears in `email_outbox_messages` table with Status=Queued
2. Within ~1 minute, the processor picks it up and sends via SMTP
3. Status changes to Sent, SentAt is populated
4. The actual email arrives in the recipient's inbox with the polished template

- [ ] **Step 2: Commit any fixes**

---

## Chunk 2: Campaign System

### Task 12: Campaign Domain Entities

**Files:**
- Create: `src/Humans.Domain/Enums/CampaignStatus.cs`
- Create: `src/Humans.Domain/Entities/Campaign.cs`
- Create: `src/Humans.Domain/Entities/CampaignCode.cs`
- Create: `src/Humans.Domain/Entities/CampaignGrant.cs`
- Modify: `src/Humans.Domain/Entities/User.cs`
- Modify: `src/Humans.Domain/Entities/EmailOutboxMessage.cs`

- [ ] **Step 1: Create CampaignStatus enum**

```csharp
namespace Humans.Domain.Enums;

public enum CampaignStatus
{
    Draft = 0,
    Active = 1,
    Completed = 2
}
```

- [ ] **Step 2: Create Campaign entity**

Properties per spec. Navigation: `ICollection<CampaignCode> Codes`, `ICollection<CampaignGrant> Grants`, `User CreatedByUser`.

- [ ] **Step 3: Create CampaignCode entity**

Properties per spec. Navigation: `Campaign Campaign`, `CampaignGrant? Grant`.

- [ ] **Step 4: Create CampaignGrant entity**

Properties per spec including `LatestEmailStatus` and `LatestEmailAt`. Navigation: `Campaign Campaign`, `CampaignCode Code`, `User User`, `ICollection<EmailOutboxMessage> OutboxMessages`.

- [ ] **Step 5: Add UnsubscribedFromCampaigns to User**

Add to `User.cs`:

```csharp
public bool UnsubscribedFromCampaigns { get; set; }
```

- [ ] **Step 6: Uncomment CampaignGrant navigation on EmailOutboxMessage**

Now that CampaignGrant exists, add/uncomment the navigation property.

- [ ] **Step 7: Build to verify**

Run: `dotnet build src/Humans.Domain/Humans.Domain.csproj`

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(campaigns): add Campaign, CampaignCode, CampaignGrant entities"
```

### Task 13: Campaign EF Configuration & Migration

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/CampaignConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/CampaignCodeConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/CampaignGrantConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/EmailOutboxMessageConfiguration.cs`

- [ ] **Step 1: Create all three EF configurations**

**CampaignConfiguration:**
- Table: `campaigns`
- Status as string conversion
- Title max 200, EmailSubject max 1000
- FK to User (CreatedByUserId)

**CampaignCodeConfiguration:**
- Table: `campaign_codes`
- Unique index on (CampaignId, Code)
- Code max 200
- FK to Campaign with cascade delete

**CampaignGrantConfiguration:**
- Table: `campaign_grants`
- Unique index on (CampaignId, UserId)
- Unique index on (CampaignCodeId)
- LatestEmailStatus as string conversion
- FKs to Campaign, CampaignCode, User

**EmailOutboxMessageConfiguration update:**
- Add FK to CampaignGrant (SetNull on delete)

- [ ] **Step 2: Add DbSets to HumansDbContext**

```csharp
public DbSet<Campaign> Campaigns { get; set; } = null!;
public DbSet<CampaignCode> CampaignCodes { get; set; } = null!;
public DbSet<CampaignGrant> CampaignGrants { get; set; } = null!;
```

- [ ] **Step 3: Create migration**

Run: `dotnet ef migrations add AddCampaigns --project src/Humans.Infrastructure --startup-project src/Humans.Web`

Verify migration creates three tables with correct constraints.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(campaigns): add EF configurations and migration for campaign tables"
```

### Task 14: CampaignService

**Files:**
- Create: `src/Humans.Application/Interfaces/ICampaignService.cs`
- Create: `src/Humans.Infrastructure/Services/CampaignService.cs`
- Create: `tests/Humans.Application.Tests/Services/CampaignServiceTests.cs`

- [ ] **Step 1: Define ICampaignService interface**

```csharp
namespace Humans.Application.Interfaces;

public interface ICampaignService
{
    Task<Campaign> CreateAsync(string title, string? description,
        string emailSubject, string emailBodyTemplate, Guid createdByUserId,
        CancellationToken ct = default);
    Task<Campaign?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<Campaign>> GetAllAsync(CancellationToken ct = default);
    Task ImportCodesAsync(Guid campaignId, IEnumerable<string> codes, CancellationToken ct = default);
    Task ActivateAsync(Guid campaignId, CancellationToken ct = default);
    Task CompleteAsync(Guid campaignId, CancellationToken ct = default);
    Task<WaveSendPreview> PreviewWaveSendAsync(Guid campaignId, Guid teamId, CancellationToken ct = default);
    Task<int> SendWaveAsync(Guid campaignId, Guid teamId, CancellationToken ct = default);
    Task ResendToGrantAsync(Guid grantId, CancellationToken ct = default);
    Task RetryAllFailedAsync(Guid campaignId, CancellationToken ct = default);
}
```

Plus a `WaveSendPreview` record:

```csharp
public record WaveSendPreview(
    int EligibleCount,
    int AlreadyGrantedExcluded,
    int UnsubscribedExcluded,
    int CodesAvailable,
    int CodesRemainingAfterSend);
```

- [ ] **Step 2: Write tests for CampaignService**

Key test cases:
1. **CreateAsync** — creates campaign in Draft status
2. **ImportCodesAsync** — creates CampaignCode rows, rejects duplicates
3. **ActivateAsync** — Draft→Active, fails if no codes imported
4. **SendWaveAsync** — assigns codes to team members, creates grants, queues outbox messages, excludes already-granted and unsubscribed
5. **SendWaveAsync sets ExtraHeaders** — verify outbox message has List-Unsubscribe headers and body has unsubscribe footer
6. **SendWaveAsync substitutes EmailSubject** — verify `{{Name}}` is replaced in the subject line
7. **SendWaveAsync duplicate prevention** — second wave for same team sends to nobody (all already granted)
6. **SendWaveAsync insufficient codes** — aborts if not enough codes
7. **ResendToGrantAsync** — creates new outbox message for existing grant
8. **CompleteAsync** — Active→Completed

- [ ] **Step 3: Run tests to verify they fail**

- [ ] **Step 4: Implement CampaignService**

Key implementation details for `SendWaveAsync`:
- Query team members (active, not suspended/deleted) for the given team
- Exclude users who already have a CampaignGrant for this campaign
- Exclude users where `UnsubscribedFromCampaigns = true`
- Claim available CampaignCodes (ordered by ImportedAt, Id) — LEFT JOIN to CampaignGrant, where Grant is null
- Verify count matches; abort if insufficient
- For each (user, code) pair:
  - Create CampaignGrant
  - Render email: substitute `{{Code}}` and `{{Name}}` (HTML-encoded via `WebUtility.HtmlEncode()`) in **both** `EmailBodyTemplate` and `EmailSubject`
  - Wrap body in email template (use OutboxEmailService's WrapInTemplate or a shared helper)
  - Generate unsubscribe token using `IDataProtectionProvider.CreateProtector("CampaignUnsubscribe").ToTimeLimitedDataProtector().Protect(userId, TimeSpan.FromDays(90))`
  - Add unsubscribe footer link to rendered body
  - Set `ExtraHeaders` JSON with `List-Unsubscribe` and `List-Unsubscribe-Post` headers containing the token URL
  - Create EmailOutboxMessage with CampaignGrantId, TemplateName="campaign_code"
- Save all in one transaction

- [ ] **Step 5: Run tests to verify they pass**

- [ ] **Step 6: Register CampaignService in DI**

Add to `InfrastructureServiceCollectionExtensions.cs`:
```csharp
services.AddScoped<ICampaignService, CampaignService>();
```

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(campaigns): add CampaignService with wave send, import, and lifecycle management"
```

### Task 15: Unsubscribe Endpoint

**Files:**
- Create: `src/Humans.Web/Controllers/UnsubscribeController.cs`
- Create: `src/Humans.Web/Views/Unsubscribe/Index.cshtml`
- Create: `src/Humans.Web/Views/Unsubscribe/Expired.cshtml`
- Create: `src/Humans.Web/Views/Unsubscribe/Done.cshtml`

- [ ] **Step 1: Create UnsubscribeController**

Public controller (no `[Authorize]`). Uses `IDataProtectionProvider.CreateProtector("CampaignUnsubscribe").ToTimeLimitedDataProtector()` with 90-day expiry.

```
GET  /unsubscribe/{token} → decode token, show confirmation page
POST /unsubscribe/{token} → set UnsubscribedFromCampaigns=true, show done page
```

If token is expired, show friendly expired message (not an error page).
If token is invalid, return 404.

- [ ] **Step 2: Create views**

Simple confirmation page. Expired page with friendly message: "This unsubscribe link has expired. Please use the link from a more recent email, or sign in to manage your preferences."

- [ ] **Step 3: Build and test manually**

Generate a test token, visit the URL, verify the flow works.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(campaigns): add unsubscribe endpoint with Data Protection tokens"
```

---

## Chunk 3: Admin UI & Self-Service

### Task 16: Admin Email Outbox Dashboard

**Files:**
- Create: `src/Humans.Web/Views/Admin/EmailOutbox.cshtml`
- Modify: `src/Humans.Web/Controllers/AdminController.cs`

- [ ] **Step 1: Add EmailOutbox action to AdminController**

Query `email_outbox_messages` for:
- Stats: count by status (Queued, Sent in last 24h, Failed)
- Recent messages: last 50 ordered by CreatedAt desc
- Pause status: read from SystemSettings

Actions:
- `POST /Admin/EmailOutbox/Pause` — set `IsEmailSendingPaused=true`
- `POST /Admin/EmailOutbox/Resume` — set `IsEmailSendingPaused=false`
- `POST /Admin/EmailOutbox/Retry/{id}` — reset message to Queued status
- `POST /Admin/EmailOutbox/Discard/{id}` — delete the failed message

- [ ] **Step 2: Create the view**

Follow existing admin page patterns (Bootstrap cards, Font Awesome icons). Stats cards at top, pause/resume button, message table below.

- [ ] **Step 3: Add nav link**

Add "Email Outbox" link to admin section in `_Layout.cshtml` or wherever admin nav is defined.

- [ ] **Step 4: Build and test**

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(email): add admin email outbox dashboard with pause/resume"
```

### Task 17: Admin Campaign Pages

**Files:**
- Create: `src/Humans.Web/Controllers/CampaignController.cs`
- Create: `src/Humans.Web/Views/Campaign/Index.cshtml`
- Create: `src/Humans.Web/Views/Campaign/Create.cshtml`
- Create: `src/Humans.Web/Views/Campaign/Detail.cshtml`
- Create: `src/Humans.Web/Views/Campaign/SendWave.cshtml`

- [ ] **Step 1: Create CampaignController**

`[Authorize(Roles = "Admin")]`, route prefix `/Admin/Campaigns`.

Actions:
- `GET  Index` — list all campaigns with stats
- `GET  Create` — form for new campaign
- `POST Create` — submit form → `ICampaignService.CreateAsync`
- `GET  Detail/{id}` — campaign detail with stats + grants table
- `POST ImportCodes/{id}` — CSV file upload → `ICampaignService.ImportCodesAsync`
- `POST Activate/{id}` → `ICampaignService.ActivateAsync`
- `POST Complete/{id}` → `ICampaignService.CompleteAsync`
- `GET  SendWave/{id}` — team selector + preview
- `POST SendWave/{id}` — confirm → `ICampaignService.SendWaveAsync`
- `POST Resend/{grantId}` → `ICampaignService.ResendToGrantAsync`
- `POST RetryAllFailed/{id}` → `ICampaignService.RetryAllFailedAsync`

- [ ] **Step 2: Create Campaign List view (Index.cshtml)**

Table: Title, Status badge, Codes (total/assigned), Sent, Failed, Created date. "New Campaign" button.

- [ ] **Step 3: Create Campaign Create view**

Form with: Title, Description, Email Subject, Email Body Template (textarea, large). Hint about `{{Code}}` and `{{Name}}` placeholders.

- [ ] **Step 4: Create Campaign Detail view**

Header with title + status + action buttons. Stats cards (Imported, Available, Sent, Failed). Grants table with name, code, assigned date, email status, resend button.

- [ ] **Step 5: Create Send Wave view**

Team dropdown (query all teams). Preview section (updates via form post or AJAX). Confirm button.

- [ ] **Step 6: Add CSV import handler**

Parse uploaded CSV file (one code per line, trim whitespace, skip empty lines). Call `ICampaignService.ImportCodesAsync`.

- [ ] **Step 7: Add nav link**

Add "Campaigns" to admin nav in `_Layout.cshtml`.

- [ ] **Step 8: Build and test all flows**

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(campaigns): add admin campaign management pages"
```

### Task 18: Self-Service Code Lookup

**Files:**
- Modify: `src/Humans.Web/Views/Profile/Index.cshtml` (or Dashboard)
- Modify: relevant controller

- [ ] **Step 1: Add campaign grants query to profile**

Query `CampaignGrant` for the current user, joined to Campaign and CampaignCode. Filter to Active/Completed campaigns only. Pass to the view.

- [ ] **Step 2: Add codes section to profile view**

Below existing profile content, add a "My Codes" section (only visible if the user has grants). Show campaign title, code, and assignment date.

- [ ] **Step 3: Build and test**

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(campaigns): add self-service code lookup on profile page"
```

### Task 19: Feature Documentation

**Files:**
- Create: `docs/features/XX-email-outbox.md`
- Create: `docs/features/XX-campaigns.md`
- Modify: `CLAUDE.md` (if needed)

- [ ] **Step 1: Write email outbox feature doc**

Business context, how it works, configuration, admin dashboard reference.

- [ ] **Step 2: Write campaigns feature doc**

Business context, workflow, authorization, unsubscribe mechanism, self-service lookup.

- [ ] **Step 3: Update CLAUDE.md if needed**

Add references to new feature docs, mention the outbox pattern, campaign system.

- [ ] **Step 4: Update DATA_MODEL.md**

Add the 4 new entities and their relationships.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "docs: add email outbox and campaign feature documentation"
```

### Task 20: Final Integration & QA

- [ ] **Step 1: Run full test suite**

Run: `dotnet test Humans.slnx`
Expected: All pass

- [ ] **Step 2: Build and deploy to QA**

Run: `bash /opt/docker/human/deploy-qa.sh`

- [ ] **Step 3: QA test the full flow**

1. Check email outbox dashboard — verify existing email types now flow through outbox
2. Trigger a verification email — verify immediate delivery (not 60s delay)
3. Create a test campaign with 3 codes
4. Send wave to Board team (small group)
5. Verify Board members received emails with their codes
6. Check grants table shows Sent status
7. Test resend functionality
8. Send second wave to Volunteers — verify Board members excluded
9. Test global pause — pause, trigger email, verify it queues but doesn't send, resume
10. Test unsubscribe link from a campaign email
11. Check self-service code lookup on profile

- [ ] **Step 4: Push to origin**

```bash
git push origin main
```
