# Team-Level Coordinator Message (Email All Rotas in a Team)

**Status:** spec / awaiting Peter review · **Branch:** `feat/shifts-team-rotas-message` · **Date:** 2026-05-27
**Driving rules:** [`docs/architecture/peters-hard-rules.md`](../../architecture/peters-hard-rules.md), reuse-first discipline (CLAUDE.md), [`docs/sections/Shifts.md`](../../sections/Shifts.md)
**Builds on:** existing per-rota coordinator message feature (`IRotaCoordinatorMessageService.SendRotaMessageAsync`, `EmailRota` action, issue nobodies-collective/Humans#732)

## Goal

Give a department coordinator a single action that sends a personalised email to **every active signup across all current/upcoming rotas in a team** — the team-level analog of the existing per-rota "Email everyone with a shift" action. Each recipient sees their own shift list (across all included rotas) plus the coordinator's message. Replies route to the coordinator (`Reply-To`), not the shared `humans@` mailbox.

## Non-goals

- Changing the `From:` header. We keep `From = humans@nobodies.team` and `Reply-To = coordinator's email`, mirroring the per-rota feature. (Peter confirmed during brainstorm — the existing Reply-To behaviour is sufficient.)
- Re-visiting the per-rota UI. The existing compose view already shows recipient count in two places; no change there.
- Per-rota selection UI. The audience is computed from "team's current/upcoming rotas" — no rota checklist.
- Sending across teams, or to people who are team members but have no active signup.

## Surface

### Entry point

`Views/ShiftAdmin/Index.cshtml` — new action card at the bottom, sibling to the existing "Create a rota" form. Brief copy ("Send a message to everyone signed up across this team's upcoming rotas") and a single **Compose…** button that navigates to the new compose page. Visibility gate matches the per-rota "Email" link: rendered only when `CanManageDepartmentAsync(user, team)` is true.

### Routes

Mirror the per-rota shape, drop `rotaId`:

| Verb | Path | Action |
|------|------|--------|
| GET  | `/Teams/{slug}/Shifts/Email` | render compose form |
| POST | `/Teams/{slug}/Shifts/Email` | dispatch |

Both live on `ShiftAdminController` (same controller as the per-rota actions).

### Compose view

`Views/ShiftAdmin/EmailTeamRotas.cshtml` — visually identical to `EmailRota.cshtml`:

- Left column: message textarea (1–4000 chars), validation summary, submit button with the count baked into the label (`Send to {N} humans`), Cancel link back to `/Teams/{slug}/Shifts`.
- Right column: recipients card with count in the header (`Recipients ({N})`) and an unordered list of recipient names. The card additionally surfaces `RotaCount` ("…across {RotaCount} rotas") so the coordinator sees the scope.
- Same `RecipientCount == 0 → disabled submit` behaviour and empty-state copy.
- Breadcrumb: Teams → Shifts → "Email everyone".
- `TempData` toast on POST success/error (reuses `vc:temp-data-alerts`).

## Service

Per the **Reuse-First Change Discipline** (CLAUDE.md): extend the existing interface rather than add a sibling service.

### Interface additions — `IRotaCoordinatorMessageService`

```csharp
Task<TeamRotasMessageDispatchResult> SendTeamRotasMessageAsync(
    Guid teamId,
    Guid senderUserId,
    string messageText,
    CancellationToken ct);

Task<TeamRotasRecipientPreview> GetTeamRotasRecipientPreviewAsync(
    Guid teamId,
    CancellationToken ct);

public sealed record TeamRotasMessageDispatchResult(
    bool Succeeded,
    int RecipientCount,
    int RotaCount,
    string TeamName,
    string? Error = null);

public sealed record TeamRotasRecipientPreview(
    string TeamName,
    int RotaCount,
    IReadOnlyList<string> RecipientNames);
```

`RecipientCount` is derivable from `RecipientNames.Count`; not stored separately to avoid drift.

### Repository ownership

Per Peter's hard rules ("A table must only exist in one repository"), the Rota table is owned by **`ShiftManagementRepository`** — that's where the rota CRUD and `GetRotasByDepartmentAsync(teamId, eventSettingsId, ct)` lookup live today. The new team-level path uses that repository for rota discovery instead of inventing a parallel `IRotaRepository`. The existing `IShiftSignupRepository.GetRotaWithShiftsAsync` cross-table read used by the per-rota path is recognised tech debt and not extended here.

`RotaCoordinatorMessageService`'s constructor therefore picks up one additional same-section repository dependency: `IShiftManagementRepository`. Service-to-repository within a section is the canonical layer call per the hard rules.

### Implementation — `RotaCoordinatorMessageService`

Internal refactor: pull the per-recipient personalisation + dispatch loop out of `SendRotaMessageAsync` into a private helper shared by both public methods:

```csharp
// Generic over TRequest so we don't introduce a new public interface
// (ICoordinatorMessageRequest) just to share the loop. Per-rota path
// closes TRequest = CoordinatorRotaMessageRequest, team path closes
// TRequest = CoordinatorTeamRotasMessageRequest.
private async Task<DispatchSummary> DispatchToRecipientsAsync<TRequest>(
    IReadOnlyList<RotaSignupGroup> rotaGroups,        // active signups partitioned by rota
    UserInfo sender,
    Func<UserInfo, IReadOnlyList<RotaShiftGroup>, TRequest> buildRequest,
    Func<TRequest, CancellationToken, Task> enqueue,
    CancellationToken ct);

private sealed record RotaSignupGroup(
    Guid RotaId,
    string RotaName,
    EventSettings EventSettings,                       // carries the rota's timezone
    IReadOnlyList<ShiftSignup> Signups);
```

Each rota carries its own `EventSettings` (and therefore its own timezone), so the helper takes signups partitioned by rota — the per-rota path passes a single-element list, the team path passes one element per included rota. The shift-line formatter (`BuildShiftLines`) is invoked **per rota group** so each rota's shifts render in its own timezone, then the recipient's lines are concatenated/grouped for the rendered body.

The helper:

1. Flattens all signups across rota groups, then groups by `UserId` (dedupe — one email per human even if they have signups across multiple rotas).
2. Fetches recipient + sender `UserInfo` via `IUserServiceRead.GetUserInfosAsync` (one round-trip).
3. Per recipient: for each rota the recipient has signups in, builds shift lines in that rota's timezone (chronological), producing a list of `RotaShiftGroup(RotaName, ShiftLines)`. Single-rota call collapses naturally to one group.
4. Calls `requestBuilder` (per-rota path returns `CoordinatorRotaMessageRequest`, team path returns `CoordinatorTeamRotasMessageRequest`) and the matching `enqueue` lambda calling `IEmailService.Send…Async`.
5. Per-recipient enqueue failures isolated by try/catch + counter, matching today's behaviour (separate `skipped` and `failed` counts kept).
6. Returns `(QueuedCount, SkippedCount, FailedCount, RecipientNames)` for the caller to log + audit.

`SendTeamRotasMessageAsync` orchestration:

1. Resolve active event via `IShiftManagementRepository.GetActiveEventSettingsAsync(ct)`. If none → `Failure("No active event")`.
2. Load team via `ITeamServiceRead.GetTeamByIdAsync(teamId, ct)` (cross-section read).
3. Load rotas in the team for the active event via `IShiftManagementRepository.GetRotasByDepartmentAsync(teamId, eventSettings.Id, ct)`, then filter to `r.Shifts.Any(s => s.EndsAt > clock.GetCurrentInstant())`. If the existing method does not eager-load `Shifts`, add a sibling `GetRotasByDepartmentWithShiftsAsync(...)` — least-surface variant.
4. Load active signups for those rotas via `IShiftSignupRepository.GetActiveByRotaIdsAsync(rotaIds, ct)` — new batch method on the existing repository, analog of `GetActiveByRotaAsync`.
5. Partition signups by `RotaId`, build `RotaSignupGroup`s, call `DispatchToRecipientsAsync`.
6. Single audit row (see §Audit).
7. Return `TeamRotasMessageDispatchResult`.

`GetTeamRotasRecipientPreviewAsync` reuses steps 1–4 to feed the GET action, then returns names + counts only (no email dispatch).

### What stays the same on the per-rota path

`SendRotaMessageAsync`'s public signature, return type, audit, and email rendering are unchanged. The only edit is calling the new shared helper for the dispatch loop. No behavioural change visible to callers or recipients.

## Audience

**Included rotas** = rotas where `Rota.TeamId == teamId`, scoped to the currently active `EventSettings`, AND `Rota.Shifts.Any(s => s.EndsAt > now)`. "Has at least one shift not yet ended" — covers rotas mid-event and rotas not yet started, excludes finished events. Active-event scoping matches the existing `GetRotasByDepartmentAsync(teamId, eventSettingsId)` lookup; out-of-event rotas (past burns) are not surfaced.

**Recipients** = distinct users with `ShiftSignup.State ∈ { Pending, Confirmed }` on any shift in any included rota.

**Per-recipient body** lists *their own* shifts in the included rotas only, grouped by rota name (alphabetical), chronological within each rota, formatted in the rota's timezone via the existing NodaTime helper.

**Empty audience** (0 recipients) is a valid display state — form renders, submit disabled, no dispatch.

## Email composition

### Request DTO

```csharp
public sealed record CoordinatorTeamRotasMessageRequest(
    Guid RecipientUserId,
    string RecipientEmail,
    string RecipientDisplayName,
    string SenderDisplayName,
    string SenderEmail,
    string TeamName,
    string MessageText,
    IReadOnlyList<RotaShiftGroup> ShiftGroups);    // grouped by rota for rendering

public sealed record RotaShiftGroup(string RotaName, IReadOnlyList<string> ShiftLines);
```

### Renderer — `IEmailRenderer.RenderCoordinatorTeamRotasMessage`

- **Subject:** new localization key `Email_CoordinatorTeamRotasMessage_Subject`, formatted with `{TeamName}`.
- **Body:** HTML, same composition pattern as `RenderCoordinatorRotaMessage`:
  - sender line (display name + `mailto:` to coordinator),
  - message text (HTML-encoded, newlines → `<br />`),
  - "Your shifts in {TeamName}:" header,
  - per `RotaShiftGroup`: rota name bolded, then `<ul><li>` of shift lines (HTML-encoded).
- Composed by `IEmailBodyComposer` for the standard footer/unsubscribe wrap; plain-text fallback auto-generated.

### Email service — `OutboxEmailService`

New method `SendCoordinatorTeamRotasMessageAsync(CoordinatorTeamRotasMessageRequest req, CancellationToken ct)`, structurally identical to `SendCoordinatorRotaMessageAsync`:

- renders via `IEmailRenderer`,
- enqueues to outbox with `MessageCategory.VolunteerUpdates`,
- sets `ReplyTo = req.SenderEmail`,
- `From` left to global `Email:FromAddress` config (= `humans@nobodies.team`).

## Authorization & audit

### Authorization

Reuse `ShiftAdminController.ResolveDepartmentManagementAsync(slug)` → `CanManageDepartmentAsync` (VolunteerManager OR DeptCoordinator; NoInfoAdmin excluded). No new policy, no new claim. POST carries `[ValidateAntiForgeryToken]`.

### Audit

- New enum value `AuditAction.CoordinatorTeamRotasMessageSent`.
- One audit row per dispatch (not per recipient), written from `SendTeamRotasMessageAsync` after the dispatch loop completes.
- `EntityType = "Team"`, `EntityId = teamId`. Audit is the horizontal section; recording a Team-section entity ID from a Shifts-section caller is a tag, not a cross-section repository/DbContext call, so it does not violate the hard rules. Pattern matches existing Shifts→Team audit references already in use.
- Payload (JSON): `{ teamSlug, teamName, recipientCount, rotaCount, messageExcerpt }` (excerpt: first 200 chars of message text, mirrors per-rota audit).

## Section boundaries (sanity)

This feature stays inside the Shifts section's call graph:

- **Controller → Service:** `ShiftAdminController` → `IRotaCoordinatorMessageService` (same section).
- **Service → Service (cross-section, read-only):** `IRotaCoordinatorMessageService` → `ITeamServiceRead.GetTeamBySlugAsync` / `GetTeamByIdAsync` (Teams section, via the `IServiceRead` contract — the approved cross-section pattern).
- **Service → Service (cross-section, read-only):** → `IUserServiceRead.GetUserInfosAsync` (Users section, already used by the per-rota path).
- **Service → Service:** → `IEmailService` (Email cross-cut).
- **Service → Repository (same section):** `IShiftManagementRepository` (owns Rota + EventSettings tables), `IShiftSignupRepository` — both Shifts-owned.
- **Service → Audit:** `IAuditService` (horizontal section).

No new cross-section coupling, no new DbContext access from a non-repository, no service reaching into another section's repository.

## Files

### New

- `src/Humans.Application/Services/Shifts/Models/TeamRotasMessageDispatchResult.cs`
- `src/Humans.Application/Services/Shifts/Models/TeamRotasRecipientPreview.cs`
- `src/Humans.Application/Services/Email/Models/CoordinatorTeamRotasMessageRequest.cs` (+ `RotaShiftGroup`)
- `src/Humans.Web/Models/Shifts/EmailTeamRotasViewModel.cs`
- `src/Humans.Web/Views/ShiftAdmin/EmailTeamRotas.cshtml`
- New `AuditAction.CoordinatorTeamRotasMessageSent` enum value
- New localization keys (EN + ES):
  - `EmailTeamRotas_Title`, `EmailTeamRotas_Heading`, `EmailTeamRotas_Breadcrumb`,
  - `EmailTeamRotas_MessageLabel`, `EmailTeamRotas_MessageHelp`,
  - `EmailTeamRotas_Send`, `EmailTeamRotas_RecipientsHeading`, `EmailTeamRotas_NoRecipients`,
  - `EmailTeamRotas_RotaCount`,
  - `Email_CoordinatorTeamRotasMessage_Subject`,
  - `ShiftAdmin_Index_EmailEveryoneCard_Heading`, `ShiftAdmin_Index_EmailEveryoneCard_Body`, `ShiftAdmin_Index_EmailEveryoneCard_Cta`.

### Modified

- `src/Humans.Application/Interfaces/Shifts/IRotaCoordinatorMessageService.cs` — add 2 methods + 2 records.
- `src/Humans.Application/Services/Shifts/RotaCoordinatorMessageService.cs` — implement new methods, factor private dispatch helper, inject `IShiftManagementRepository`, no behavioural change to existing method.
- `src/Humans.Application/Interfaces/Shifts/IShiftSignupRepository.cs` — add `GetActiveByRotaIdsAsync(IReadOnlyCollection<Guid> rotaIds, CancellationToken)` (batch analog of `GetActiveByRotaAsync`).
- `src/Humans.Infrastructure/Repositories/Shifts/ShiftSignupRepository.cs` — implement the multi-rota variant.
- `src/Humans.Application/Interfaces/Shifts/IShiftManagementRepository.cs` + `src/Humans.Infrastructure/Repositories/Shifts/ShiftManagementRepository.cs` — if `GetRotasByDepartmentAsync` doesn't already eager-load `Shifts` + `EventSettings`, add `GetRotasByDepartmentWithShiftsAsync(teamId, eventSettingsId, ct)`. If it does, reuse as-is.
- `src/Humans.Application/Interfaces/Email/IEmailService.cs` + `src/Humans.Application/Services/Email/OutboxEmailService.cs` — add `SendCoordinatorTeamRotasMessageAsync`.
- `src/Humans.Infrastructure/Services/EmailRenderer.cs` — add `RenderCoordinatorTeamRotasMessage`.
- `src/Humans.Web/Controllers/ShiftAdminController.cs` — two new actions (`EmailTeamRotas` GET + POST).
- `src/Humans.Web/Views/ShiftAdmin/Index.cshtml` — new "Email everyone in upcoming rotas" card at the bottom.
- `src/Humans.Domain/Enums/AuditAction.cs` — new enum value.
- Resource files: `Strings.en.resx`, `Strings.es.resx` (and any Email-specific resx the existing renderer reads from).

## Tests

Follow the test patterns the per-rota feature established. No EF-InMemory (per [`memory/architecture/no-ef-inmemory.md`](../../../memory/architecture/no-ef-inmemory.md) or current test-suite reshaping direction).

### Service unit tests — `RotaCoordinatorMessageServiceTests.TeamScoped`

- **Recipient dedup:** user with signups in 3 rotas in the team receives **one** email, not 3.
- **Upcoming/current filter:** rota whose every shift is in the past is excluded; rota with a future shift is included; rota mid-event (started, not ended) is included.
- **State filter:** Withdrawn/Cancelled signups excluded; Pending + Confirmed included.
- **Per-recipient body:** each recipient's body lists only their shifts, grouped by rota name, chronological within rota.
- **Per-recipient failure isolation:** mock `IEmailService` to throw on recipient #2; recipients #1 and #3 still receive; `FailureCount = 1`.
- **Audit written once** with correct `EntityType="Team"`, `EntityId=teamId`, and payload counts.
- **Zero-recipient case:** matches per-rota convention — returns `Failure("no active signups…")`, no email enqueued, no audit row. (The GET form already disables submit at 0 recipients; the service guard is defence-in-depth.)
- **Preview helper:** returns same names/counts as a dry-run dispatch.
- **Per-rota path unchanged:** existing `SendRotaMessageAsync` tests continue to pass after the helper extraction.

### Controller tests — `ShiftAdminControllerTests.EmailTeamRotas`

- **Authorization:** non-coordinator → 403 / forbidden; VolunteerManager → 200; DeptCoordinator → 200; NoInfoAdmin → 403.
- **Anti-forgery:** POST without token → 400.
- **GET happy path:** view model populated with team name, recipient count + names, rota count.
- **POST happy path:** calls `SendTeamRotasMessageAsync`, sets success `TempData`, redirects to `/Teams/{slug}/Shifts`.
- **POST validation:** empty message → re-renders view with validation error, no service call.
- **POST zero recipients:** service returns `Failure`; controller surfaces error `TempData` and re-renders the form (same as per-rota).
- **POST service failure:** error `TempData`, re-render or redirect (match per-rota convention).

### Renderer test

- `RenderCoordinatorTeamRotasMessage` produces subject with team name; body contains sender mailto, HTML-encoded message text, per-rota shift groups in expected HTML shape.

### E2E (if the per-rota path has Playwright coverage)

Parallel scenario: coordinator logs in, navigates to `/Teams/{slug}/Shifts`, clicks "Compose…", sees recipient count, types message, sends, sees toast. Otherwise skip — don't add E2E unilaterally.

## Risks & open questions

- **Email fan-out volume.** A team with many active signups across many rotas could send hundreds of emails. Outbox handles delivery rate, but the dispatch loop holds the request open until enqueue completes. If this becomes painful, move dispatch behind a background job (Hangfire) — not in scope here, but trivial follow-up.
- **Subject template wording (EN/ES).** Peter to confirm wording when implementing localisation. Default proposal: EN `"Message from your {TeamName} coordinator"`, ES `"Mensaje del coordinador de {TeamName}"`.
- **"Upcoming/current" cut precision.** Spec defines `Shifts.Any(s => s.EndsAt > now)`. Alternative would be `StartsAt > now` (excludes mid-event). Going with `EndsAt` to match "current"; flip if Peter wants stricter "future-only".

## Out of scope

- Background-job dispatch (see risks).
- Multi-team broadcast (e.g., "all rotas in all teams I coordinate").
- Coordinator selection UI for rotas within the team.
- Modifying the `From:` header (Peter declined — Reply-To suffices).
- Confirm-before-send modal or banner-style count (Peter declined — existing side-card + count-in-button treatment carries over).
