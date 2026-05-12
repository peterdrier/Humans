# Mailer Section — Inbound MailerLite Import (Slice 1)

**Status:** Design — awaiting user approval before plan.
**Author / facilitator:** brainstorming session 2026-05-11 → 2026-05-12.
**Related issues:** nobodies-collective/Humans#450 (umbrella bidirectional infra), #200 (push opt-ins + timestamp conflict resolution), #205 (contact tier — already largely landed via `ContactSource` + `AccountProvisioningService`), #524 (admin-hosted newsletter signup — separate later slice).

> **Post-implementation note (2026-05-12):** The `forgotten_emails` skip-list described below was removed during PR review — it isn't needed. When a user is deleted, Humans already removes them from MailerLite; if they later re-add themselves on ML, that's fine and they can be re-imported. The deletion-cascade write and the classifier skip step are gone; the rest of the spec is historically accurate.

## 1. Context

Humans needs to integrate with MailerLite bidirectionally. Tonight's slice ships **inbound-only**: an admin-driven, human-in-the-loop import that pulls ML subscribers and reconciles them against Humans users + `CommunicationPreference[Marketing]` state. Outbound (push to ML), webhooks, group sync, and GDPR forget cascade are deferred to later slices.

The slice deliberately establishes the **Mailer section** (new), the typed read-only ML client, the dashboard surface, and the plan/apply orchestration that future automated runs (Hangfire job, ML webhook) will reuse unchanged.

## 2. Scope

### In scope tonight

- New section `Mailer` with section invariants doc.
- Read-only typed client `IMailerLiteService` (subscribers + groups + account summary).
- `IMailerImportService` with split `BuildPlanAsync` / `ApplyAsync`.
- `/Mailer/Admin` dashboard (live ML stats + Humans-side counts + last-reconciliation summary).
- `/Mailer/Admin/Import` preview screen — classifies every ML subscriber, shows counts and per-row outcome, requires admin "Commit Import" to execute.
- New column `CommunicationPreference.SubscribedAt` (nullable `Instant`).
- New table `forgotten_emails` (skip-list to prevent GDPR-anonymized-user resurrection).
- New `AuditAction.MailerLiteReconciliationCompleted` enum value.
- Tests: classification matrix, conflict rule, idempotency, architecture pins, controller smoke.
- Updates to existing section docs (Profiles, AuditLog, Users) to reflect the new column / action / table.

### Out of scope (later slices)

- Outbound push (Humans → ML) — no write methods on the client; outbound is its own slice with its own scrutiny.
- Webhooks from ML — needs public preview-env routing and dashboard config.
- Group / mailing-list membership sync — tonight we read group *counts* for the dashboard but do not map ML groups to Humans concepts.
- Engagement metrics (open/click rates).
- `ContactSource.SelfSignup` (#524's territory).
- `#204` marketing-opt-in rollout campaign.
- Auto-scheduled reconciliation. Admin button only.

## 3. Architecture

### New section: `Mailer`

Orchestration section. Owns one small table (`forgotten_emails`); writes through other sections' services for everything else. Pattern matches Onboarding (orchestrator section, minimal own state).

```
src/Humans.Application/Interfaces/Mailer/
  IMailerLiteService.cs              ← typed client surface (GET only)
  IMailerImportService.cs            ← BuildPlan + Apply
  MailerLiteOptions.cs               ← API key + base URL binding
  Dtos/
    MailerLiteSubscriber.cs          ← shape confirmed via 2026-05-12 probe (§13 below)
    MailerLiteGroup.cs               ← shape confirmed via 2026-05-12 probe (§13 below)
    MailerLiteAccountSummary.cs
    ImportPlan.cs                    ← classified subscriber list + counts
    ImportResult.cs                  ← post-apply counts

src/Humans.Application/Services/Mailer/
  MailerImportService.cs             ← orchestrator; Application layer; no DbContext

src/Humans.Application/Interfaces/Repositories/
  IForgottenEmailRepository.cs

src/Humans.Application/Services/Users/AccountLifecycle/
  ForgottenEmailService.cs           ← writes to forgotten_emails on anonymization
                                        (owned by Users — that's where the trigger lives)

src/Humans.Infrastructure/Services/Mailer/
  MailerLiteClient.cs                ← HttpClient impl; GET-only runtime guard

src/Humans.Infrastructure/Repositories/Mailer/
  ForgottenEmailRepository.cs

src/Humans.Web/Controllers/Mailer/
  MailerAdminController.cs           ← /Mailer/Admin, /Mailer/Admin/Import[/Commit]

src/Humans.Web/Views/Mailer/Admin/
  Index.cshtml                       ← dashboard
  Import.cshtml                      ← preview + commit
```

### Layer compliance

- `MailerImportService` lives in Application and **never imports** `Microsoft.EntityFrameworkCore`. Cross-section writes route through service interfaces: `IUserService`, `IUserEmailService`, `IAccountProvisioningService`, `ICommunicationPreferenceService`, `IAuditLogService`.
- `MailerLiteClient` lives in Infrastructure (owns `HttpClient` + JSON DTOs).
- `ForgottenEmailService` lives under Users/AccountLifecycle (next to `AccountDeletionService`) because that's where the trigger fires; the table is owned by Mailer logically (only Mailer reads it) but write-trigger ownership sits with the deletion cascade.

  Design choice: the table physically lives in the Mailer section's directory (`Configurations/Mailer/`) but is *written* by the Users-section anonymization cascade and *read* by Mailer's import classifier. Both reads and writes go through `IForgottenEmailRepository` so there's a single SQL surface.

### Dependencies on other sections

| Need | Service called | Direction |
|---|---|---|
| Lookup verified user by email | `IUserEmailService.FindVerifiedEmailWithUserAsync` | read from Profiles |
| Lookup any user by email | `IUserEmailService.FindAnyUserIdByEmailAsync` | read from Profiles |
| Delete an unverified UserEmail row | `IUserEmailService.RemoveAsync` (or equivalent — TBD on existing surface) | write to Profiles |
| Create contact-only User | `IAccountProvisioningService.FindOrCreateUserByEmailAsync` | write to Users |
| Read/write Marketing pref | `ICommunicationPreferenceService.GetAsync` / `UpdatePreferenceAsync` | read+write Profiles |
| Tombstone redirect | `IUserService.GetMergedSourceIdsAsync` (inverse direction handled per-call) | read from Users |
| Audit | `IAuditLogService.LogAsync` (job overload) | write to AuditLog |
| Count ML-sourced contacts | new `IUserService.GetCountByContactSourceAsync(ContactSource)` (likely new) | read from Users |
| Count Marketing opt-in/out | new `ICommunicationPreferenceService.GetCountByCategoryAndStateAsync(MessageCategory, optedOut)` (likely new) | read from Profiles |

The two "likely new" repository methods get added with this slice. They're trivial COUNT queries.

## 4. Data Model Changes

### New column: `communication_preferences.SubscribedAt`

```csharp
public class CommunicationPreference
{
    // ...existing fields...

    /// <summary>
    /// Earliest opt-in instant we know about for this category.
    /// - Written on first opt-in (OptedOut transitions false→true... err, true→false).
    /// - Written on first import from an external source carrying a real subscribe date.
    /// - Preserved across opt-out / re-opt cycles — represents "first ever subscribed", not "currently subscribed since".
    /// - Null for rows that pre-date this column or were lazy-seeded as default-opted-out.
    /// </summary>
    public Instant? SubscribedAt { get; set; }
}
```

Nullable, no backfill of existing rows. Migration is a single `ADD COLUMN`. Touched by:
- `CommunicationPreferenceService.UpdatePreferenceAsync` — when an `OptedOut: true → false` transition is happening and `SubscribedAt is null`, stamp it to `now`.
- `MailerImportService.ApplyAsync` — when ML reports a `subscribed_at` and our row has `SubscribedAt is null`, stamp it from ML.

### New table: `forgotten_emails`

```csharp
public class ForgottenEmail
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }              // the now-anonymized user
    public string EmailHash { get; init; } = "";   // SHA-256 of NormalizeForComparison(email)
    public Instant AnonymizedAt { get; init; }
}
```

**Indexes:** `EmailHash` (lookup), `(UserId, EmailHash)` unique (idempotency), `AnonymizedAt`.

Reasoning: store the **hash** not the plaintext email so the GDPR-deletion guarantee isn't undermined by keeping the address around in a side table. Lookup is by hash, which is fine — we hash incoming ML subscriber emails before checking.

Written when `AnonymizeExpiredAccountAsync` runs (one row per deleted `UserEmail`). Read when `MailerImportService.BuildPlanAsync` classifies subscribers. Never updated; eventual cleanup policy TBD (probably never delete — GDPR favours suppression-list persistence).

### New enum value: `AuditAction.MailerLiteReconciliationCompleted`

Job-level summary entry written at the end of `ApplyAsync`. Description carries counts as a structured string. Existing `CommunicationPreferenceChanged`, `ContactCreated`, `UserEmailDeleted` cover per-row events. No other new `AuditAction` values needed.

## 5. Matching Ladder + Conflict Rules

### Per-subscriber classification

For each ML subscriber pulled (cursor through `GET /api/subscribers` until exhausted — `meta.next_cursor` is the page-forward token):

```
// Drop entries that haven't completed double-opt-in.
// MailerLite status values seen in live data: active, unconfirmed, unsubscribed,
// bounced, junk. "unconfirmed" = signed up but no double-opt-in yet — not a real
// subscription. If they confirm later they'll show as "active" on the next run.
if subscriber.status == "unconfirmed":
    classification = UnconfirmedSkipped
    continue

hash = SHA256(NormalizeForComparison(subscriber.email))

if forgotten_emails.exists(hash):
    classification = ForgottenSkipped
    continue

verified = IUserEmailService.FindVerifiedEmailWithUserAsync(subscriber.email)
if verified != null:
    target = follow MergedToUserId tombstone chain
    classification = AttachVerified(targetUserId)
    continue

unverifiedMatch = IUserEmailService.FindAnyUserIdByEmailAsync(subscriber.email)
if unverifiedMatch != null:
    classification = DeleteUnverifiedThenCreate(unverifiedUserId, unverifiedEmailId)
    continue

classification = CreateContact

if multiple verified users match the same email:
    classification = AmbiguousMultipleVerified(userIds[])
    // hit before AttachVerified above; FindVerifiedEmailWithUserAsync currently returns
    // ambiguous-match cases as null — see "Open items" §11
```

### Conflict rule for AttachVerified prefs

After classification (with `unconfirmed` already filtered out above), when applying an `AttachVerified` decision we may or may not flip the Marketing pref:

```
mlState = subscriber.status == "active" ? OptedOut=false : OptedOut=true
// MailerLite date format is "YYYY-MM-DD HH:MM:SS" (no offset). Treat as UTC.
// For unsubscribed/bounced/junk records, subscribed_at is set + unsubscribed_at is set.
// For active records, unsubscribed_at is null.
mlActionAt = subscriber.unsubscribed_at ?? subscriber.subscribed_at

humansPref = CommunicationPreferenceService.GetAsync(userId, Marketing)

isHumansUserAction = humansPref.UpdateSource in {"Profile", "Guest", "MagicLink", "OneClick"}

// Bounce/junk always override — these are delivery facts, not preferences
if subscriber.status in ("bounced", "junk"):
    apply mlState with UpdateSource="MailerLiteSync"

// User-action conflict resolution
elif isHumansUserAction and humansPref.UpdatedAt > mlActionAt:
    keep humans state, no write

else:
    apply mlState with UpdateSource="MailerLiteSync", UpdatedAt=mlActionAt

// SubscribedAt: stamp on first known opt-in, never overwrite
if subscriber.subscribed_at != null and humansPref.SubscribedAt is null:
    humansPref.SubscribedAt = subscriber.subscribed_at
```

### Idempotency rules

- `CommunicationPreferenceChanged` audit only fires when `OptedOut` actually flips (not on confirm-state-already-correct passes).
- `SubscribedAt` never overwrites a non-null value.
- `ContactSource` never overwrites a non-null value (existing `SetContactSourceIfNullAsync` behavior).
- `forgotten_emails` insert is idempotent on `(UserId, EmailHash)`.
- Whole `ApplyAsync` is re-runnable; a second consecutive run against unchanged ML state writes zero rows and one summary audit.

## 6. Dashboard — `/Mailer/Admin`

Single page, no tabs. Renders on every load (live ML hit + Humans counts).

### Panes

**ML Side**
- Live API calls per page load: `GET /api/groups` (one call — also carries per-group counts: active/unsubscribed/unconfirmed/bounced/junk natively), plus one `GET /api/subscribers?filter[status]={status}&limit=1` per status bucket if we want global totals (these `filter[status]` queries return `meta.total` cheaply; subscribers can be in multiple groups so summing group counts would double-count).
- Total subscribers globally, broken by status: active / unsubscribed / unconfirmed / bounced / junk
- Groups list: name + subscriber counts per group (active/unsubscribed/unconfirmed/bounced/junk — all available natively)
- "Last fetched: just now"

**Humans Side** (two DB count queries)
- Users with `ContactSource = MailerLite`: *N*
- Marketing opted-in: *N*
- Marketing opted-out: *N*
- Forgotten-email skip-list size: *N*

**Drift indicator**
- Compare `ML active count` vs `Humans Marketing opted-in count`
- "ML has 487 active, Humans has 482 opted-in. Likely 5 behind." (Or "in sync.")

**Last reconciliation**
- Pulled from latest `audit_log` entry where `Action = MailerLiteReconciliationCompleted`
- Timestamp + the summary string
- Link to `/Board/AuditLog?action=MailerLiteReconciliationCompleted` for history

**Actions**
- Button: "Run Import →" routes to `/Mailer/Admin/Import` (preview)

### 6.1 Drift Report panel

Sits beneath the headline counts. Surfaces the bidirectional disagreements that inbound-only can't fully close — these are the active-compliance-risk surface until outbound ships.

Three rows, each with a count + click-through to a detail list:

| Drift type | Count | What it means | Risk |
|---|---|---|---|
| H− / M+ — Humans says opted-out, ML still active | *N* | User unsubscribed in Humans; ML hasn't been told; **ML is still sending to them** | **legal-trouble** until manually fixed in ML |
| H+ / M? — Humans opted-in, not in ML | *N* | User opted in via Humans; never propagated to ML; **they aren't receiving newsletters they want** | service quality, not legal |
| Forgotten / M+ — GDPR-deleted, ML still active | *N* | User anonymized in Humans; ML keeps their subscription; **ML still sending to a deleted user** | GDPR-compliance concern |

Click-through opens a list view with: redacted-email preview, Humans userId (link to `/Profile/{id}/Admin`), and a deep link to that subscriber in MailerLite's UI (`https://dashboard.mailerlite.com/subscribers/single/{id}`) so admin can resolve the drift by manually unsubscribing / re-subscribing / deleting on ML's side.

The H− / M+ row is the legal-priority drift. If it's non-zero on dashboard load, render with red emphasis.

These counts come from cross-referencing the in-memory plan from the dashboard's live ML pull against Humans's pref state — no new DB queries beyond what the dashboard already runs.

## 7. Import Preview + Commit — `/Mailer/Admin/Import`

### `GET /Mailer/Admin/Import` — preview

Calls `IMailerImportService.BuildPlanAsync()`:
1. Page through ML subscribers (`limit=100` per call until exhausted)
2. Classify each per §5 ladder
3. Return `ImportPlan { Counts, Decisions[] }`

Renders the **headline counts table**:

| Outcome | Count |
|---|---|
| Total ML subscribers | N |
| Will create contact | N₁ |
| Will attach to verified user — pref flip | N₂ₐ |
| Will attach to verified user — confirm-only | N₂ᵦ |
| Will keep Humans state (conflict — Humans newer user-action) | N₂ᵧ |
| Will delete unverified row, then create contact | N₃ |
| Skipped — forgotten (GDPR) | N₄ |
| Skipped — ambiguous multi-match | N₅ |
| Skipped — unconfirmed (no double-opt-in) | N₆ |

Then per-category **collapsible detail tables**. Each row: redacted email preview (`pe***@nob***.team` for the admin-list view; full email on row expand), ML status, ML last-action timestamp, matched user (display name + id if matched), what will happen.

For category 3 (delete unverified): full email shown by default — admin specifically needs to eyeball these.
For category 5 (ambiguous): both/all matched userIds listed with deep links to `/Admin/DuplicateAccounts` for resolution before re-running.

Big "Commit Import" button at the bottom.

### `POST /Mailer/Admin/Import/Commit`

Calls `BuildPlanAsync` again (fresh — stateless preview/commit), then `ApplyAsync(freshPlan)`.

If `Counts` differ from a snapshot stored in TempData by more than 10% in any non-zero category, redirect to preview with banner: "Plan changed since preview — review and re-confirm." This is the only stateful concession to the otherwise-stateless model.

`ApplyAsync`:
- Executes decisions in batches (50 per `TransactionScope`).
- Errors per-batch are logged and counted but don't abort the run — partial completion is acceptable; failures show in the summary audit.
- Writes one final `MailerLiteReconciliationCompleted` audit with full counts.
- Returns `ImportResult` to controller; redirect to `/Mailer/Admin` with success banner.

## 8. MailerLite Client Surface (GET-only invariant)

### `IMailerLiteService`

```csharp
public interface IMailerLiteService
{
    Task<MailerLiteAccountSummary> GetAccountSummaryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MailerLiteGroup>> ListGroupsAsync(CancellationToken ct = default);
    IAsyncEnumerable<MailerLiteSubscriber> ListSubscribersAsync(
        CancellationToken ct = default);
    Task<MailerLiteSubscriber?> GetSubscriberAsync(
        string email, CancellationToken ct = default);
}
```

That's the **entire** surface. No `CreateSubscriberAsync`, no `UpdateSubscriberAsync`, no `DeleteSubscriberAsync`, no method prefixed `Set`/`Add`/`Remove`/`Upsert`. Outbound is a separate slice with its own review.

### Runtime + analyzer enforcement

`MailerLiteClient` implementation has one private `SendAsync` helper:

```csharp
private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
{
    if (req.Method != HttpMethod.Get)
        throw new InvalidOperationException(
            $"Mailer client is read-only. Attempted {req.Method} {req.RequestUri}. " +
            "Outbound writes belong to a separate slice with its own review.");
    return await _http.SendAsync(req, ct);
}
```

Architecture test pins the interface shape:

```csharp
[Fact]
public void IMailerLiteService_HasNoWriteMethods()
{
    var forbidden = new[] { "Create", "Update", "Delete", "Upsert", "Add", "Remove", "Set", "Post", "Put", "Patch" };
    var methods = typeof(IMailerLiteService).GetMethods();
    foreach (var m in methods)
        foreach (var prefix in forbidden)
            m.Name.Should().NotStartWith(prefix);
}
```

### Auth + retry

- API key from `IOptions<MailerLiteOptions>`. Binds to config key `MailerLite:ApiKey` (user-secrets in dev, env var `MailerLite__ApiKey` or flat `MAILERLITE_API_KEY` in PR/prod).
- Base URL: `https://connect.mailerlite.com` (per public docs).
- Bearer token auth via `Authorization: Bearer {key}`.
- Version pin: `X-Version: 2038-01-01` (or latest stable — confirm during probe).
- HTTP client registered via `IHttpClientFactory` with Polly retry: 3 attempts, exponential backoff, only on 5xx/timeout. **No retry on 4xx** — they're our bugs.
- Rate-limit awareness: respect `X-RateLimit-Remaining` header, back off when low.

## 9. Audit Events

| Event | Fires when | Action enum value |
|---|---|---|
| `ContactCreated` (existing) | `AccountProvisioningService` creates new contact during category-`CreateContact` apply | existing |
| `UserEmailDeleted` (existing) | unverified row deleted in category-`DeleteUnverifiedThenCreate` apply | existing |
| `CommunicationPreferenceChanged` (existing) | Marketing `OptedOut` actually flips, OR a sync-driven write changes any tracked field (e.g., first-time `SubscribedAt` stamp), OR a new contact's first Marketing row is seeded | existing |
| `MailerLiteReconciliationCompleted` (new) | End of every `ApplyAsync` run — success, partial, or failure | **new** |

### Audit-every-pref-write invariant (legal-grade)

**No code path writes to `CommunicationPreference[Marketing]` without producing a `CommunicationPreferenceChanged` audit entry.** Three guarantees stack:

1. All writes route through `CommunicationPreferenceService.UpdatePreferenceAsync` (the existing service surface already audits user-driven writes; we must verify it covers sync-driven writes and seed writes too — Open Item §11.6).
2. An architecture test forbids any service outside `CommunicationPreferenceService` from injecting `ICommunicationPreferenceRepository` or `HumansDbContext` and writing the table directly.
3. The audit entry's `Description` field encodes: previous `OptedOut` state, new state, `UpdateSource`, and the *user-action timestamp* (which may differ from `OccurredAt` for sync writes — sync writes carry the ML-side click time).

Idempotent confirms (reconciliation re-evaluates a row and decides no change is needed) do **not** write a per-row audit — that would flood the log. The job-level `MailerLiteReconciliationCompleted` entry establishes "we evaluated this user today" for the whole batch.

### Summary entry description format

```
MailerLite reconciliation: 487 pulled,
  3 contacts created,
  8 prefs updated, 0 prefs preserved by conflict-rule,
  2 unverified rows deleted-and-superseded,
  1 ambiguous skipped, 0 forgotten skipped, 4 unconfirmed skipped,
  4.2s elapsed, 12 ML API calls, 0 errors.
```

No per-subscriber PII in the summary — only counts. Per-row entries (existing actions) reference user ids, which is correct — they're already the audit surface for those events.

### What lives outside our audit

- **ML's own activity log per subscriber** — `GET /api/subscribers/{id}/activity` carries ML's full lifecycle. We don't mirror it. For forensics on a specific subscriber, query ML directly at the moment in question.
- **Per-send "we believed they were opted-in at send time"** — that belongs on `email_outbox_messages` rows (the outbox is the send-evidence surface), not the audit log. Out of scope tonight because Humans is not currently the marketing sender; deferred until §14.

## 9.1 Reliability invariants — "no missed changes"

Operating model: ML is the sender. The Mailer section's job is to keep Humans and ML *agreeing* on who is subscribed, with a propagation delay measured in hours-to-overnight (never weeks). These invariants are the load-bearing guarantees that nightly sync produces.

1. **Idempotent apply.** A second `ApplyAsync` run against unchanged state on both sides writes zero per-row entries and one summary audit. (Already in §5.)

2. **Process-everyone-or-fail-loud.** `BuildPlanAsync` records the set of subscriber IDs it pulled. `ApplyAsync` tracks which IDs reached a final classification. At end, `pulled.Count == classified.Count`. Any mismatch is captured in the summary as `N skipped due to processing error`, with the per-error details written to log (Serilog) at Error level. The job never silently drops a subscriber.

3. **Run liveness signal.** Every cycle ends with a `MailerLiteReconciliationCompleted` audit, even on partial failure (description carries error counts). Dashboard's "Last reconciliation" reads the latest one. When the cadence becomes nightly (next slice), an alert fires if no entry has been written in >36h.

4. **Cross-side disappearance detection.** Subscribers that were classified in run N but absent from ML's pull in run N+1 mean one of:
   - ML deleted them via `forget` API — fine, no action needed on our side.
   - Pagination skipped them in the current run — drift, must be detected.
   `ApplyAsync` includes a reconciliation step: for every Humans user with `ContactSource=MailerLite`, check that the user's verified emails were in the current pull. Any miss → log warning + add to summary count `N existing-contacts not seen in this pull`. This catches pagination bugs in the client.

5. **Bidirectional commitment.** Inbound-only is a temporary state. **Outbound (Humans → ML) is the next slice and must ship before any other Mailer feature.** Until it does, drift in the Humans-newer-than-ML direction (§5 conflict-rule "keep Humans state" outcomes) is a known compliance gap mitigated only by the dashboard's drift report (§6.1) and manual admin remediation. This invariant is restated in `docs/sections/Mailer.md` as a top-of-doc warning.

6. **No Humans-initiated marketing emails until send-time guardrail exists.** All marketing currently flows through ML. If Humans ever gains marketing-send capability, a single `IEmailService` choke point must check `Marketing.OptedOut` at send time and stamp the answer onto the `email_outbox_messages` row. Out of scope tonight; explicit deferral in §14.

## 10. Future Hooks (deferred)

Designed so these slot in without restructuring the Application layer:

- **Hangfire recurring job** → calls `IMailerImportService.BuildPlanAsync` + `ApplyAsync` directly. No controller layer involved.
- **ML webhook listener** → new `MailerWebhookController` constructs a one-decision `ImportPlan` from the webhook payload and calls `ApplyAsync(plan)`. Same write path.
- **Outbound sync** → new `IMailerLiteWriteService` (separate interface, separate slice, separate code review). The read interface stays untouched.

## 11. Open Items

These need resolution before / during implementation:

1. **Existing `IUserEmailService` deletion path.** Need to find the existing method that deletes an unverified `UserEmail` row through the orchestrator (with invariant + cache invalidation). If none exists, add `RemoveUnverifiedAsync(userEmailId, userId, reason)` to the interface.

2. **`FindVerifiedEmailWithUserAsync` ambiguity behavior.** Method currently returns a single match (or null) — need to confirm it surfaces "multiple matches" rather than silently picking one. If it doesn't, add `FindAllVerifiedAsync` for our classifier.

3. **`SubscribedAt` write site on native opt-in.** Confirm `CommunicationPreferenceService.UpdatePreferenceAsync` is the right place to stamp it on user-initiated opt-in (likely yes — it's the single write surface).

4. **Forgotten-email retroactive backfill.** Existing users who've already been anonymized: their emails aren't in `forgotten_emails`. Three options: backfill from audit (`AccountAnonymized` entries don't store emails — would need audit description parsing, fragile); accept the gap and document; add a one-shot admin tool. Tentatively: accept the gap, document. At our scale and recency, the number affected is likely zero or near-zero.

5. **Global subscriber totals for the dashboard.** Two strategies considered (see §6): fan out one `filter[status]` query per status bucket, or sum group counts (incorrect — double-counts cross-group subscribers). Going with the fan-out. Five tiny queries per dashboard load is fine. Verify `meta.total` is returned on filtered queries during impl.

## 12. Acceptance Criteria

- [ ] `docs/sections/Mailer.md` exists with the standard invariant-doc structure.
- [ ] `IMailerLiteService` interface has only GET-shaped methods; architecture test pins this.
- [ ] `MailerLiteClient` runtime-rejects non-GET HTTP methods.
- [ ] `MailerLiteOptions` binds from both `MailerLite:ApiKey` and flat `MAILERLITE_API_KEY` env var.
- [ ] `IMailerImportService.BuildPlanAsync` returns a categorized plan matching §5 classifications.
- [ ] `ApplyAsync` is idempotent: a second run against unchanged ML state writes zero non-summary rows.
- [ ] Conflict rule preserves Humans state only when `UpdateSource ∈ {Profile, Guest, MagicLink, OneClick}` AND `UpdatedAt > mlActionAt`.
- [ ] Bounced/junk subscribers always set `OptedOut = true` regardless of Humans timestamp.
- [ ] `CommunicationPreference.SubscribedAt` added; migration empty `Down()` not required (additive).
- [ ] `forgotten_emails` table created; `AccountDeletionService.AnonymizeExpiredAccountAsync` writes one row per deleted UserEmail.
- [ ] `MailerImportService` filters incoming subscribers against `forgotten_emails` by hash.
- [ ] Dashboard at `/Mailer/Admin` renders all panes from §6.
- [ ] Preview at `/Mailer/Admin/Import` shows headline counts + per-category detail.
- [ ] Commit re-fetches and re-classifies; redirects to preview if count delta > 10% in any category.
- [ ] One `MailerLiteReconciliationCompleted` audit per apply run; per-row `CommunicationPreferenceChanged` only on actual flips.
- [ ] No PII in the summary audit description; only counts.
- [ ] `AdminOnly` policy on all `/Mailer/Admin/*` routes.
- [ ] Tests: classifier matrix (all five outcomes), conflict rule (timestamps + bounce override + UpdateSource narrowing), idempotency, GET-only architecture pin, dashboard count math.
- [ ] Updates to `docs/sections/Profiles.md` (SubscribedAt), `docs/sections/AuditLog.md` (new action), `docs/sections/Users.md` (forgotten_emails write), `memory/INDEX.md` if any new atom emerges.

## 13. MailerLite API shape (confirmed by probe 2026-05-12)

Two read-only calls were made against the live production MailerLite account to ground the DTOs. No PII appears below — only field names + types. The probes used the API key from `user-secrets` (key `MailerLite:ApiKey`), authenticated with `Authorization: Bearer {key}`, against base URL `https://connect.mailerlite.com`.

### `GET /api/subscribers?limit=2`

Top-level response: `{ data: Subscriber[], links: {...}, meta: {...} }`.

Pagination: cursor-based. `meta.next_cursor` is the page-forward token (also exposed as `links.next` URL). `prev_cursor` exists. **Not** page-numbered.

Subscriber fields:

| Field | Type | Nullable? | Notes |
|---|---|---|---|
| `id` | string | no | **String, not numeric.** |
| `email` | string | no | |
| `status` | string | no | Seen values: `active`, `unconfirmed`. Documented values per ML: `active`, `unsubscribed`, `unconfirmed`, `bounced`, `junk`. |
| `source` | string | no | e.g., "manual", "api", "form" |
| `subscribed_at` | string ("YYYY-MM-DD HH:MM:SS") | yes | Null for `unconfirmed` subscribers. UTC. |
| `unsubscribed_at` | string ("YYYY-MM-DD HH:MM:SS") | yes | Null when not unsubscribed. UTC. |
| `opted_in_at` | string ("YYYY-MM-DD HH:MM:SS") | yes | Double-opt-in confirmation timestamp; separate from `subscribed_at`. |
| `created_at` | string ("YYYY-MM-DD HH:MM:SS") | no | |
| `updated_at` | string ("YYYY-MM-DD HH:MM:SS") | no | |
| `fields` | object | no | Custom + standard fields, sub-keys include: `name`, `last_name`, `company`, `city`, `country`, `state`, `z_i_p`, `phone` |
| `ip_address` | string | yes? | GDPR-relevant; **do not store** — out of scope |
| `optin_ip` | string | yes? | GDPR-relevant; **do not store** — out of scope |
| `sent` | number | no | engagement — out of scope |
| `opens_count` | number | no | engagement — out of scope |
| `open_rate` | number | no | engagement — out of scope |
| `clicks_count` | number | no | engagement — out of scope |
| `click_rate` | number | no | engagement — out of scope |

For our `MailerLiteSubscriber` DTO we read only: `id`, `email`, `status`, `source`, `subscribed_at`, `unsubscribed_at`, `opted_in_at`, `fields.name`, `fields.last_name`. Everything else is ignored.

### `GET /api/groups`

Top-level: `{ data: Group[], links: {...}, meta: {...} }`.

Pagination: classic Laravel-style page numbers (`meta.current_page`, `meta.last_page`, `meta.total`, `meta.per_page`). **Different scheme from subscribers** — the client needs to handle both.

Group fields:

| Field | Type | Notes |
|---|---|---|
| `id` | string | |
| `name` | string | |
| `created_at` | string ("YYYY-MM-DD HH:MM:SS") | |
| `active_count` | number | |
| `unsubscribed_count` | number | |
| `unconfirmed_count` | number | |
| `bounced_count` | number | |
| `junk_count` | number | |
| `sent_count` | number | engagement |
| `opens_count` | number | engagement |
| `open_rate` | object | **object**, not a flat number — shape TBD during impl |
| `clicks_count` | number | engagement |
| `click_rate` | object | object, not flat |

For our `MailerLiteGroup` DTO we read: `id`, `name`, `created_at`, and the five per-status counts. Engagement fields ignored.

### Date parsing

ML's date format is `"YYYY-MM-DD HH:MM:SS"` (no `T`, no offset). Parser must accept that exact pattern and treat as UTC → NodaTime `Instant`. Custom converter needed; `DateTimeOffset.Parse` won't reliably handle the space separator across cultures.

### Rate limit

Headers not inspected during this probe (call count was 2, well under any conceivable limit). To confirm during impl: `X-RateLimit-Remaining`, `X-RateLimit-Limit`, `Retry-After`. Public ML docs say 120 req/min.

## 14. Not in this spec (explicit deferrals)

- **Outbound push (Humans → MailerLite) — the very next slice, not a vague future.** Inbound-only state is a known compliance gap (see §9.1 invariant 5). Outbound must ship before any other Mailer feature. It covers: Humans-side `Marketing.OptedOut` changes propagated to ML's `PATCH /api/subscribers/{id}` (or unsubscribe endpoint), GDPR anonymization cascaded to ML's `POST /api/subscribers/{id}/forget`, and pairing with inbound on the same nightly cadence.
- Humans-initiated marketing emails. All marketing currently flows through ML. Any future Humans-side marketing send requires a single `IEmailService` choke point that checks `Marketing.OptedOut` at send time and stamps the answer onto the `email_outbox_messages` row (§9.1 invariant 6). Out of scope tonight.
- Webhook listener and HMAC verification. Useful for sub-hour latency on ML-side events but not required at "overnight is fine" SLA; reconciliation covers the cases. Defer until SLA tightens.
- Group → Humans concept mapping. Dashboard shows group counts from ML; we don't infer Humans-side meaning from them.
- Engagement metrics (opens/clicks). Their fields are visible on subscriber records (see §13) but not pulled or stored.
- IP addresses (`ip_address`, `optin_ip` on ML subscriber records). GDPR-sensitive; no value in mirroring; never read by our DTO.
- `#204` marketing opt-in rollout campaign — separate UX work blocked on legal review.
- Automated scheduling — Hangfire job comes after manual import has lived in production for at least one cycle AND outbound has shipped (the nightly job should sync both ways in one pass).
