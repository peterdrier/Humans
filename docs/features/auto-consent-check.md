# Auto Consent Check (LLM-backed)

## Business Context

The Consent Check is the final step of the standard onboarding pipeline: after a new human has completed their profile and signed all required legal documents, a Consent Coordinator reviews the profile before the human is auto-approved as a Volunteer. In practice the overwhelming majority of these reviews are rubber-stamps — the legal name looks like a real name and nothing about the profile is concerning. The Consent Coordinator's real value is catching the rare case that *isn't* routine (profanity, impersonation, a specific individual that needs human attention).

This feature automates the rubber-stamp cases with an LLM and keeps the manual-review queue clear for the interesting ones. It ONLY ever approves — it never flags, never rejects, never downgrades any profile. If the model isn't confident, or the API is unavailable, or anything else goes wrong, the entry is left exactly where it was and a human reviews it as before.

## Scope — what the job does / doesn't do

**Does:**
- Polls the Consent Check = Pending queue every 15 minutes.
- For each entry, asks Claude Haiku two questions: does the legal name look like a real human name, and does it match any entry on the admin-maintained hold list?
- If the answer to both is "clean" (plausible real name AND no hold-list match), clears the consent check on behalf of the system — audit action `ConsentCheckAutoCleared`, actor recorded as the job name, `ConsentCheckedByUserId` left null.
- Continues the normal post-clear flow: `IsApproved = true`, nav/notification cache invalidation, `IFullProfileInvalidator` refresh, Volunteers team sync, Colaborador/Asociado sync if relevant.
- Audits every decision (cleared or skipped) with the model id and the LLM's stated reason.

**Does NOT:**
- Flag profiles. The manual flag path (`IOnboardingService.FlagConsentCheckAsync`) is still the only way a profile ends up in the Flagged bucket.
- Reject profiles. Rejection remains a Board/Admin action.
- Call the LLM if the kill switch is set to `None`.
- Touch profiles in any state other than `ConsentCheckStatus = Pending, IsApproved = false, RejectedAt = null`.
- Persist any human text from the LLM in a user-facing field. The reason is stored only in the internal `ConsentCheckNotes` (admin-visible) and in the audit log.

## Third-Party Processing Disclosure (GDPR)

The LLM assistant sends the human's **legal name** (first + last) and the full admin-maintained hold list to Anthropic's Claude API. Anthropic acts as a **third-party processor** for the purpose of evaluating the name. No other personal data is transmitted — not email, not city, not IP, not profile picture, not contact fields.

Per Anthropic's current published policy, API requests are not used to train models by default and are retained only for a short abuse-monitoring window. Operators deploying this system are responsible for confirming the up-to-date processor terms match their data-protection agreements with humans (Article 28 GDPR). If this disclosure is unacceptable, set `SyncServiceType.AutoConsentCheck` to `None` at `/Google/SyncSettings` — the job will skip without any outbound traffic and the manual Consent Check flow continues as before.

## Workflow

```
Every 15 min (cron */15)
    │
    ▼
AutoConsentCheckJob
    │
    ├── Kill switch: SyncMode.None → log + exit
    │
    ├── Load pending userIds: IOnboardingService.GetPendingConsentCheckUserIdsAsync
    ├── Load hold list:       IConsentHoldListService.ListAsync
    ├── Batch-load profiles:  IProfileService.GetByUserIdsAsync
    │
    ▼ for each pending user
    │
    ├── legalName = FirstName + ' ' + LastName
    ├── verdict = IConsentCheckAssistant.EvaluateAsync(legalName, holdList)
    │       ↓ HTTP POST https://api.anthropic.com/v1/messages
    │       ↓ model: claude-haiku-4-5 (configurable)
    │       ↓ 10s timeout, 1 retry on 5xx / 429 / timeout
    │       ↓ parse strict JSON: { plausible_real_name, hold_list_match, reason }
    │
    ├── if PlausibleRealName && !HoldListMatch:
    │       IOnboardingService.AutoClearConsentCheckAsync
    │           → sets Cleared + IsApproved=true, audit ConsentCheckAutoCleared
    │           → triggers Volunteers team sync
    │
    └── else (or any exception):
            audit ConsentCheckAutoSkipped with the reason + model id
            leave entry in the manual queue
```

## Data Model

### `ConsentHoldListEntry` (table `consent_hold_list`)

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `int` (auto-inc PK) | |
| `Entry` | `string` required, max 500 | Free-form name/alias text. |
| `Note` | `string?` max 2000 | Admin-only context about why this entry exists. |
| `AddedByUserId` | `Guid` | FK to `users`, `OnDelete: Restrict`. |
| `AddedAt` | `NodaTime.Instant` | |

### Audit entries

| Action | Written by | Actor |
|--------|-----------|-------|
| `ConsentHoldListEntryAdded` | `ConsentHoldListService.AddAsync` | Admin user |
| `ConsentHoldListEntryRemoved` | `ConsentHoldListService.DeleteAsync` | Admin user |
| `ConsentCheckAutoCleared` | `OnboardingService.AutoClearConsentCheckAsync` | `AutoConsentCheckJob` (job name, no actor) |
| `ConsentCheckAutoSkipped` | `AutoConsentCheckJob` (direct) | `AutoConsentCheckJob` |

## Configuration

| Key | Meaning |
|-----|---------|
| `Anthropic:ApiKey` (env `Anthropic__ApiKey`) | Anthropic API key. Required for the assistant; without it calls throw and every run skips. Treat as a secret. |
| `Anthropic:Model` | Optional. Defaults to `claude-haiku-4-5`. Override for canary testing of newer Haiku versions. |
| `SyncServiceType.AutoConsentCheck` at `/Google/SyncSettings` | Kill switch. `None` = job exits immediately. Anything else (e.g. `AddOnly`, `AddAndRemove`) = enabled. The Add/Remove semantics are meaningless for this service; any non-None value is treated as "on". |

The Admin Configuration page (`/Admin/Configuration`) shows whether `Anthropic:ApiKey` is set. The key is marked sensitive and never displayed.

## Admin UI

- `/Admin/ConsentHoldList` — list, add, remove hold-list entries. Admin-only. Linked from `/Admin` (Admin Tools landing page).

## Failure modes

| Failure | Behaviour |
|---------|-----------|
| API key missing | `EvaluateAsync` throws; job logs warning and skips the entry; no outbound traffic. |
| HTTP 5xx / 429 | One retry; if still failing, skip the entry. Audit `ConsentCheckAutoSkipped` not written (we only audit LLM-returned verdicts, not transport errors — the job logs them). |
| HTTP 4xx | No retry. Skip the entry with a logged warning. |
| Timeout (10s) | One retry. If still timing out, skip the entry. |
| Malformed JSON in response | Skip the entry with a logged warning. |
| LLM returns `plausible_real_name = false` | Skip, audit `ConsentCheckAutoSkipped`. Manual reviewer handles it. |
| LLM returns `hold_list_match = true` | Skip, audit `ConsentCheckAutoSkipped`. Manual reviewer handles it. |
| `AutoClearConsentCheckAsync` returns non-success | Skip with a logged warning (e.g. profile was rejected or cleared between queue fetch and approval). |

## Open design notes / deferrals

- **No per-run cost cap.** At our scale (target ~500 humans total, ~a few new signups per week) and Haiku pricing, the unconstrained cost is negligible. If traffic rises, a simple per-run ceiling should go in the job before the LLM loop. TODO tracked in the PR description.
- **Retroactive backlog.** On first deploy, the job will pick up every Pending entry that already exists. This is intentional — the backlog is exactly the workload the feature is meant to reduce.
- **Hold list seeding.** The list starts empty. Admins add entries as specific situations arise.
- **No standalone metric counter.** The existing `IHumansMetrics.RecordJobRun("auto_consent_check", ...)` covers job-level success/failure. Per-verdict counters can be added later if the auto-clear rate becomes a thing to tune.

## Related Features

- `docs/features/16-onboarding-pipeline.md` — the broader onboarding flow this fits into.
- `docs/features/17-coordinator-roles.md` — ConsentCoordinator role whose workload this reduces.
- `docs/sections/onboarding.md` — section invariants, triggers, negative rules.
- `docs/features/12-audit-log.md` — where the four new AuditAction values land.
