# Email Outbox & Campaign System Design

**Date:** 2026-03-14
**Status:** Draft

## Problem

The Humans system sends emails inline via SMTP. If a send fails (429 rate limit, network error, etc.), the email is lost with no way to recover. Additionally, the org needs to send individualized discount codes to members (presale tickets) with guarantees about delivery tracking and the ability to send in waves for testing.

## Goals

1. **Reliable email delivery** — no email is lost on transient failure; all sends are tracked with retry
2. **Branded email template** — light polish to match the website's Renaissance/parchment aesthetic
3. **Campaign system** — import codes, assign to filtered humans in waves, track delivery, support resend
4. **Observability** — Prometheus metrics for queue depth, sent/failed counts; admin dashboard
5. **Unsubscribe** — campaign emails include unsubscribe mechanism per Google/Yahoo bulk sender requirements
6. **Self-service code lookup** — humans can view their assigned campaign codes

## Non-Goals

- Priority lanes (transactional vs campaign) — FIFO is sufficient at ~500 users
- Lottery-based code assignment — future feature for low-income ticket system
- Email open/click tracking — out of scope
- AMP email support
- Bounce processing — out of scope for v1 (see Future Considerations)

## Architecture Overview

Three interconnected features built on a shared foundation:

```
┌─────────────────────────────────────────────────┐
│                   Callers                        │
│  (Services, Jobs, Controllers, CampaignService)  │
└──────────────────────┬──────────────────────────┘
                       │ IEmailService.Send*Async()
                       ▼
┌─────────────────────────────────────────────────┐
│            OutboxEmailService (NEW)              │
│  Implements IEmailService                        │
│  Renders email → writes to outbox table          │
│  Immediate-send templates trigger Hangfire job   │
└──────────────────────┬──────────────────────────┘
                       │ INSERT
                       ▼
┌─────────────────────────────────────────────────┐
│          email_outbox_messages (DB)              │
│  Status: Queued → Sent | Failed                  │
│  Crash recovery via PickedUpAt timeout           │
└──────────────────────┬──────────────────────────┘
                       │ Polled every 1 min + on-demand
                       ▼
┌─────────────────────────────────────────────────┐
│         ProcessEmailOutboxJob (Hangfire)          │
│  Batch of 10 per cycle (10/min throttle)         │
│  Exponential backoff on failure, max 10 retries  │
└──────────────────────┬──────────────────────────┘
                       │ SMTP send
                       ▼
┌─────────────────────────────────────────────────┐
│      SmtpEmailTransport (EXISTING, renamed)      │
│  Implements IEmailTransport                      │
│  Transport-only, called by processor             │
└─────────────────────────────────────────────────┘
```

## Data Model

### EmailOutboxMessage (NEW)

Every email flows through this table — transactional and campaign alike.

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| RecipientEmail | string | Destination address |
| RecipientName | string? | Display name |
| Subject | string | Email subject line |
| HtmlBody | string | Fully rendered HTML |
| PlainTextBody | string? | Plain text fallback |
| TemplateName | string | e.g. "welcome", "campaign_code" (matches existing `RecordEmailSent` labels) |
| UserId | Guid? (FK→User) | Null for external recipients |
| CampaignGrantId | Guid? (FK→CampaignGrant) | Links campaign code delivery |
| ReplyTo | string? | Reply-to address (e.g., facilitated messages) |
| ExtraHeaders | string? | JSON dictionary for additional headers (List-Unsubscribe, etc.) |
| Status | EmailOutboxStatus enum | Queued, Sent, Failed (no Sending — see Crash Recovery) |
| CreatedAt | Instant | When queued |
| SentAt | Instant? | When accepted by SMTP server (not inbox delivery — see note) |
| PickedUpAt | Instant? | When processor claimed this message (null = not yet picked up) |
| RetryCount | int | Increments on failure, default 0 |
| LastError | string? | Truncated to 4000 chars |
| NextRetryAt | Instant? | Exponential backoff: now + 2^RetryCount minutes |

**Note on SentAt:** This records when the SMTP server accepted the message, not when it reached the recipient's inbox. Inbox delivery confirmation would require bounce processing (see Future Considerations). For admin purposes, "Sent" means "handed off to Google's SMTP relay."

**Indexes:**
- (SentAt, RetryCount, NextRetryAt, PickedUpAt) — composite for processor query
- UserId (for user email history / self-service view)
- CampaignGrantId (for campaign status tracking)

**Crash Recovery:** No `Sending` status. The processor uses a `PickedUpAt` timestamp. The processor query selects messages where `SentAt IS NULL AND RetryCount < 10 AND (NextRetryAt IS NULL OR NextRetryAt <= now) AND (PickedUpAt IS NULL OR PickedUpAt < now - 5 minutes)`. If the process crashes mid-send, the message is automatically re-picked after the 5-minute timeout. This is an improvement over the Google sync outbox's simpler `ProcessedAt` approach, adding explicit crash recovery.

**Retention:** Sent rows purged after 150 days by `CleanupEmailOutboxJob`. Failed rows retained until manually resolved. 150-day retention ensures emails cover the full event season for self-service lookup.

**Sensitive templates:** For verification emails (`email_verification`), the HtmlBody contains a time-limited token URL. These tokens expire in minutes and are harmless after expiry — no special scrubbing is needed. The outbox stores the same PII that already exists in the database (names, emails); it does not introduce a new category of sensitive data.

### Campaign (NEW)

A named code distribution with email template.

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| Title | string | "2026 Presale" |
| Description | string? | Internal notes |
| EmailSubject | string | Subject with `{{Name}}` placeholder |
| EmailBodyTemplate | string | HTML body with `{{Code}}`, `{{Name}}` placeholders |
| Status | CampaignStatus enum | Draft, Active, Completed |
| CreatedAt | Instant | |
| CreatedByUserId | Guid (FK→User) | Admin who created it |

### CampaignCode (NEW)

Imported code pool. Codes not referenced by a grant are available for assignment.

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| CampaignId | Guid (FK→Campaign) | |
| Code | string | The actual code value from CSV |
| ImportedAt | Instant | When CSV was processed |

**Unique constraint:** (CampaignId, Code)

### CampaignGrant (NEW)

Links one code to one human within a campaign.

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| CampaignId | Guid (FK→Campaign) | |
| CampaignCodeId | Guid (FK→CampaignCode) | Which code from the pool |
| UserId | Guid (FK→User) | Which human |
| AssignedAt | Instant | When code was assigned |
| LatestEmailStatus | EmailOutboxStatus? | Denormalized: Queued/Sent/Failed from latest outbox message |
| LatestEmailAt | Instant? | Denormalized: timestamp of latest email status change |

**Unique constraints:**
- (CampaignId, UserId) — one code per human per campaign; enables safe multi-wave sends
- (CampaignCodeId) — each code assigned at most once

**Send history:** Multiple EmailOutboxMessage rows can reference the same grant (initial send + resends/reminders).

### Entity Relationships

```
Campaign ──1:N──→ CampaignCode (imported pool)
Campaign ──1:N──→ CampaignGrant (assignments)
CampaignCode ──1:0..1──→ CampaignGrant (assigned or available)
CampaignGrant ──1:N──→ EmailOutboxMessage (send history)
User ──1:N──→ CampaignGrant (codes received)
User ──1:N──→ EmailOutboxMessage (email history)
```

## Feature 1: Email Outbox (Reliability)

### OutboxEmailService

New implementation of `IEmailService` that replaces `SmtpEmailService` in DI registration. Each of the 15 existing email methods:

1. Calls `IEmailRenderer` to render subject + HTML body (unchanged)
2. Wraps HTML in the email template (moved from SmtpEmailService)
3. Generates plain text fallback
4. Writes an `EmailOutboxMessage` row with Status = Queued
5. Records `emails_queued` metric
6. Returns immediately

**TemplateName** is set by each method, matching the existing labels used in `RecordEmailSent` calls (e.g., `"welcome"`, `"application_approved"`, `"email_verification"`).

**Immediate-send templates:** For time-sensitive emails (email verification, password-related), after writing to the outbox, also enqueue a one-off Hangfire job: `BackgroundJob.Enqueue<ProcessEmailOutboxJob>(x => x.ExecuteAsync(default))`. This triggers the processor immediately rather than waiting up to 60 seconds for the next recurring cycle. The recurring job remains as a safety net.

### ProcessEmailOutboxJob (Hangfire Recurring)

Runs every 1 minute (`*/1 * * * *`). Analogous in structure to `ProcessGoogleSyncOutboxJob` but with improved crash recovery via `PickedUpAt` claiming.

```
1. Check global pause flag — if paused, return immediately
2. SELECT TOP(10) FROM email_outbox_messages
   WHERE SentAt IS NULL
     AND RetryCount < 10
     AND (NextRetryAt IS NULL OR NextRetryAt <= now)
     AND (PickedUpAt IS NULL OR PickedUpAt < now - 5 minutes)
   ORDER BY CreatedAt ASC
3. Set PickedUpAt = now for entire batch, SaveChanges
4. Open single SMTP connection for batch
5. For each message:
   a. Send via MailKit (including ReplyTo and ExtraHeaders)
   b. On success: Status = Sent, SentAt = now, PickedUpAt = null
      On failure: Status = Failed, RetryCount++, LastError = message
                  NextRetryAt = now + 2^RetryCount minutes
                  PickedUpAt = null (release for future retry)
   c. Record metrics (sent/failed counter by template)
6. Close SMTP connection
7. SaveChanges
8. Set outbox_pending gauge to current queue depth
```

**Crash recovery:** If the process crashes between steps 3 and 7, the `PickedUpAt` timestamp ensures messages are re-picked after 5 minutes. No messages are ever permanently orphaned.

**SMTP connection reuse:** The processor opens one SMTP connection for the batch and reuses it for all messages, rather than connecting/disconnecting per message. This reduces overhead and avoids connection churn with Google's SMTP relay.

### Throttle Configuration

```json
"Email": {
  "OutboxBatchSize": 10,
  "OutboxMaxRetries": 10,
  "OutboxRetentionDays": 150
}
```

Default: 10 per 1-minute cycle = 10/min. Google limit is 100/min; we stay at 10% to leave headroom.

### Global Pause

A simple `bool IsEmailSendingPaused` stored in a `SystemSettings` key-value table (not `SyncServiceSettings` — email pause is a binary on/off, not a sync mode). The processor checks this at the start of each batch. Toggled via Admin UI. When paused:
- No emails are sent
- Queued emails remain in outbox
- Admin UI shows "Sending paused" indicator
- Campaigns can still queue emails (they just won't be sent until unpaused)

### IEmailTransport (NEW)

New interface for SMTP transport, replacing the public `IEmailService` methods on `SmtpEmailService`:

```csharp
public interface IEmailTransport
{
    Task SendAsync(string recipientEmail, string? recipientName,
        string subject, string htmlBody, string? plainTextBody,
        string? replyTo = null,
        IDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default);
}
```

`SmtpEmailTransport` (renamed from `SmtpEmailService`) implements `IEmailTransport`. This handles SMTP connection, MimeMessage construction (including ReplyTo header), and sending. The `extraHeaders` parameter supports List-Unsubscribe for campaign emails.

## Feature 2: Email Template Polish

### Changes to WrapInTemplate

The existing template wrapper (currently in SmtpEmailService, moving to OutboxEmailService) gets enhanced:

**Added:**
- Dark header bar (#3d2b1f) with gold "Humans" wordmark and "NOBODIES COLLECTIVE" subtitle
- Gold accent border below header (#c9a96e, 3px)
- Warm parchment footer background (#f0e2c8) with border-top
- Consistent 24px horizontal padding in body area

**Unchanged:**
- Source Sans 3 body font (with Segoe UI, Roboto, sans-serif fallbacks)
- Georgia heading font (web-safe fallback for Cormorant Garamond)
- 600px max-width
- Aged ink text color (#3d2b1f)
- Gold link color (#8b6914)
- Environment banner (QA/Staging) positioned above the header bar

**Email client safety:**
- All styles inline (Gmail strips `<style>` blocks in body)
- No background images or SVG textures
- Web fonts degrade gracefully to system fonts
- `<style>` block in `<head>` for clients that support it (Apple Mail, Thunderbird)

### CTA Button Styling

Action links (sign-in, verify email, etc.) rendered as gold pill buttons:
```html
<a href="..." style="display:inline-block;background:#c9a96e;color:#3d2b1f;
   text-decoration:none;padding:10px 24px;border-radius:4px;font-weight:600;
   font-size:14px;">Sign in to Humans</a>
```

This is opt-in per email template — the renderer marks certain links as CTAs.

## Feature 3: Campaign System

### Authorization

Campaign management requires the **Admin role**. All routes under `/Admin/Campaigns` use `[Authorize(Roles = "Admin")]`.

### Campaign Workflow

```
Create (Draft) → Import Codes → Activate → Send Wave(s) → Complete
```

**Step 1: Create Campaign**
Admin enters title, description, email subject, and HTML body template with `{{Code}}` and `{{Name}}` placeholders. Campaign starts in Draft status.

**Step 2: Import Codes**
Admin uploads CSV file with one code per row (or single column). System creates `CampaignCode` rows. Multiple imports allowed (appends to pool). Duplicate codes within campaign are rejected.

**Step 3: Activate**
Status: Draft → Active. Required before sending. Validates that at least one code is imported and email template is set. Warns (does not block) if the template body does not contain `{{Code}}`.

**Step 4: Send Wave** (repeatable)
1. Admin selects recipient filter by **team**:
   - Volunteers (the Volunteers system team — effectively "all active humans")
   - Any other team (Board, specific project teams, etc.)
2. All filters implicitly exclude suspended, deactivated, and deleted users.
3. System shows preview:
   - N humans match filter
   - Excludes M humans who already have a grant in this campaign
   - Excludes K humans who unsubscribed from campaigns
   - P codes available in pool (Q will remain after send)
4. On confirm (single transaction, default Read Committed isolation — unique constraints prevent double-assignment):
   - Claim N available codes from pool (ORDER BY ImportedAt, Id) in one batch query
   - Verify enough codes are available; abort if not
   - Create all CampaignGrant rows
   - For each grant, render email (substitute `{{Code}}` and `{{Name}}` — **values are HTML-encoded** before substitution to prevent injection)
   - Create all EmailOutboxMessage rows with CampaignGrantId
   - Commit transaction
   - If unique constraint violation (concurrent double-click), return graceful error "Wave already in progress"
5. Dashboard updates as outbox processor delivers

**Step 5: Monitor & Resend**
Campaign detail page shows all grants with delivery status. Admin can:
- Resend to individual human (queues new outbox message for same grant) — this is an explicit admin action, not subject to unsubscribe checks
- Retry all failed (re-queues all grants whose latest outbox message is Failed)

**Step 6: Complete**
Status: Active → Completed. Prevents further wave sends. Campaign + grants remain as permanent audit trail.

### Unsubscribe

**User preference:** `UnsubscribedFromCampaigns` boolean on User entity. Default: false. This applies to the user as a whole (not per email address) — if a human unsubscribes, they won't receive campaign emails regardless of which address is their notification target. Must be included in GDPR data exports. Cleared on account deletion (moot — user is deleted).

**Email headers:** Campaign emails include:
- `List-Unsubscribe: <mailto:unsubscribe@nobodies.team?subject=unsubscribe>, <https://humans.nobodies.team/unsubscribe/{token}>`
- `List-Unsubscribe-Post: List-Unsubscribe=One-Click`

**Footer link:** Campaign emails include "Don't want to receive these? [Unsubscribe](link)" in the footer.

**Endpoint:** `GET /unsubscribe/{token}` — shows confirmation page. `POST /unsubscribe/{token}` — sets the flag. Token is generated using ASP.NET `IDataProtectionProvider.CreateProtector("CampaignUnsubscribe").ToTimeLimitedDataProtector()` with a 90-day expiry, encoding the user ID. This reuses the project's existing Data Protection infrastructure (keys stored in DB via `PersistKeysToDbContext`).

**Expired token handling:** If the token has expired, show a friendly message: "This unsubscribe link has expired. Please use the link from a more recent email, or sign in to manage your preferences." Do not display an error page.

**Campaign wave sends** automatically exclude users where `UnsubscribedFromCampaigns = true`.

### Self-Service Code Lookup

Humans can view their assigned campaign codes on their profile or dashboard. This is a read-only view showing:
- Campaign title
- Assigned code
- Assignment date

Query: `CampaignGrant` rows for the current user, joined to `Campaign` (for title) and `CampaignCode` (for code value). Only shows grants from Active or Completed campaigns.

This lets humans retrieve their code if they lost the original email, without needing to contact an admin for a resend.

## Admin UI

### Campaign Management (/Admin/Campaigns)

**Campaign List:** Table with title, status badge, code counts (total/assigned), sent/failed counts, creation date. "New Campaign" button.

**Campaign Detail (/Admin/Campaigns/{id}):**
- Header: title + status badge + action buttons (Send Wave, Import Codes, Complete)
- Stats cards: Codes Imported, Available, Sent, Failed
- Grants table: human name, code (monospace), assigned timestamp, email status + timestamp, resend action

**Send Wave Dialog:**
- Filter dropdown: team selector (Volunteers = all active, or any specific team)
- Live preview: recipient count, exclusions (already granted, unsubscribed, inactive), codes remaining
- Confirm button: "Confirm Send to N Humans"

### Email Outbox Dashboard (/Admin/EmailOutbox)

**Global controls:**
- Pause/Resume button (red when active, toggles global pause flag)
- Status indicator: "Sending active" / "Sending paused"

**Stats cards:** Queued (current), Sent (24h), Failed (current), Throttle rate

**Message table:** Recent outbox messages with recipient, template name, status, timestamp, retry/discard actions for failed messages.

## Metrics (Prometheus)

Extends existing `IHumansMetrics` with new methods. Metric names use the existing dot-delimited convention:

```csharp
// New methods on IHumansMetrics:
void RecordEmailQueued(string template);   // humans.email_queued_total counter
void RecordEmailFailed(string template);   // humans.email_failed_total counter
void SetEmailOutboxPending(int count);     // humans.email_outbox_pending gauge
// Existing RecordEmailSent(template) reused for humans.emails_sent_total
```

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| humans.email_queued_total | Counter | template | Emails added to outbox |
| humans.emails_sent_total | Counter | template | Successfully delivered (existing metric, name kept for backward compat) |
| humans.email_failed_total | Counter | template | Delivery failures |
| humans.email_outbox_pending | Gauge | — | Current queue depth (set by processor each cycle) |

**Note:** `HumansMetricsService` (concrete singleton) is injected directly in Hangfire jobs, following the existing `ProcessGoogleSyncOutboxJob` pattern. The `IHumansMetrics` interface is used elsewhere.

## Migration Path

The outbox is a **breaking change** in email flow — all 15 email methods switch from inline SMTP to outbox writes. This is safe because:

1. OutboxEmailService implements the same IEmailService interface
2. Only the DI registration changes (SmtpEmailService → OutboxEmailService for IEmailService; SmtpEmailTransport for IEmailTransport)
3. The processor uses IEmailTransport internally for transport
4. If the processor isn't running, emails queue up harmlessly

**Rollback:** Keep `SmtpEmailService` as a class that still implements `IEmailService` (don't delete it). To rollback, swap DI registration from `OutboxEmailService` back to `SmtpEmailService`. Queued-but-unsent emails would need manual processing or can be drained by running the processor one last time before switching.

## DI Registration

Environment-based registration, similar to the existing Google stub pattern:

```csharp
// IEmailService: callers use this to queue emails
services.AddScoped<IEmailService, OutboxEmailService>();

// IEmailTransport: processor uses this to send
if (isStubMode)
    services.AddScoped<IEmailTransport, StubEmailTransport>(); // logs, no SMTP
else
    services.AddScoped<IEmailTransport, SmtpEmailTransport>();
```

`StubEmailTransport` is a new class replacing the previously unused `StubEmailService`. It implements `IEmailTransport` and logs the send without connecting to SMTP. In dev/test, emails still flow through the outbox (testing the full pipeline) but the transport is a no-op. The environment-based transport selection is new (email previously always used `SmtpEmailService` regardless of environment).

## Template Substitution Security

Campaign email template substitution (`{{Code}}`, `{{Name}}`) must HTML-encode all values before insertion using `System.Net.WebUtility.HtmlEncode()`. This prevents HTML injection via user-controlled data (e.g., a display name containing `<script>`). Placeholder matching uses `StringComparison.Ordinal` per CODING_RULES.md.

The complete substitution vocabulary for campaign templates:
- `{{Code}}` — the assigned code value
- `{{Name}}` — the recipient's display name

## Cleanup Job

`CleanupEmailOutboxJob` runs weekly (Sunday 03:00 UTC). Deletes outbox rows where `Status = Sent AND SentAt < now - RetentionDays`. Failed rows are never auto-deleted — admin must manually discard them via the outbox dashboard.

## Testing Strategy

- **Unit tests:** OutboxEmailService writes correct rows; ProcessEmailOutboxJob processes correctly with mock transport
- **Integration tests:** End-to-end campaign flow with test DB
- **QA testing:** Campaign workflow with real SMTP to a test mailbox. Send wave to Board (small group) first.
- **StubEmailTransport:** Used in dev/test — emails flow through outbox but transport is a no-op (logs only)

## Future Considerations

**Bounce processing:** When SMTP accepts a message but the recipient's server later rejects it (mailbox full, invalid address), a bounce email is sent back to the configured From address. Currently these accumulate unread. A future enhancement could add inbound bounce parsing to update outbox status from Sent → Bounced, enabling proactive admin notification. This is out of scope for v1 — the admin outbox dashboard lets admins spot patterns manually for now.

**Low-income ticket lottery:** The Campaign entity is designed to be reusable for future lottery-based code assignment. The assignment logic (how codes map to humans) differs, but the Campaign/CampaignCode/CampaignGrant structure and email delivery pipeline remain the same.
