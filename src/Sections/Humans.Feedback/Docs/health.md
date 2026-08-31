# Feedback — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

Keeps the archive of in-app feedback reports people filed before the section was retired, and
lets admins finish triaging them: read the queue, read one report with its conversation, reply
to the reporter (email + in-app notification), move status, assign to a person and/or team, and
link a GitHub issue. Nothing new can be filed by anyone, at any privilege. The rest of the app
asks it two questions: how many reports still need an admin, and which of one person's reports
are still open. It also answers for its data under GDPR (export, erasure) and folds rows when
two accounts merge.

## 2. The shapes

| Shape | Surface | Notes |
|---|---|---|
| Triage read | UI `GET /Feedback`, `GET /Feedback/{id}`; `IFeedbackTriage.GetFeedbackListAsync` / `GetFeedbackByIdAsync` | One question ("show me the queue / this report") with two skins: admin UI and key-authed Backdoor API |
| Triage write | UI `POST /Feedback/{id}/{Message,Status,Assignment,GitHubIssue}`; matching `IFeedbackTriage` methods | Same four verbs in both skins; every one audited, attributed to the acting human |
| Actionable-count signal | `IFeedbackServiceRead.GetActionableCountAsync` | Feeds nav pill + `/Admin` tile; cached 2 min in-service |
| Per-user open ids | `IFeedbackServiceRead.GetOpenFeedbackIdsForUserAsync` | Agent user snapshot only |
| GDPR | `IUserDataContributor` export + erase | Erase deletes own reports (+screenshots), detaches authorship elsewhere |
| Merge fold | `IUserMerge.ReassignAsync` | Re-FK both tables, called inside Profiles' merge transaction |

## 3. Structure

These shapes need exactly: one admin controller + two views (queue with detail partial), one
service implementing the four outward roles (`IFeedbackServiceRead`, `IFeedbackTriage`,
`IUserDataContributor`, `IUserMerge`), one repository over the two owned tables
(`feedback_reports`, `feedback_messages`), the nav/tile contributions, and the resx set. No
caching decorator, no orchestration, no jobs. The section is already this shape; the target is
holding it there while the archive ages — surface should only ever shrink.

## 4. Invariants

- No code path creates a `FeedbackReport` — no service method, repository write, route, or view.
- Every `/Feedback` UI route and count render is full-`Admin`; `FeedbackAdmin` alone reaches
  nothing, reporters reach nothing. The one exception is the Backdoor API skin
  (`/api/backdoor/feedback`, owned by the Backdoor section): personal-key auth via
  `BackdoorApiKeyAuthFilter`, acting human resolved from the key.
- Every message posted now is an admin reply: stamps `LastAdminMessageAt`, emails the reporter,
  dispatches an in-app notification — and the email sends **before** persisting so an SMTP
  failure leaves nothing committed (retry-safe).
- Status leaving Resolved/WontFix clears `ResolvedAt`/`ResolvedByUserId`; entering them sets both.
- Needs-reply is derived, never stored: reporter message newer than last admin reply, or Open with
  no admin reply ever; the badge count uses the same rule and excludes Resolved/WontFix.
- Status, assignment and GitHub-link changes are audit-logged with the acting user ("API" only as
  the nobody-fallback).
- Erasure keeps replies the person left on other people's reports (authorship detached) — GDPR
  Art. 17(3)(b).

## 5. Seams

None. The section is retired and closed; no specified-but-unbuilt work reserves a place here.

## 6. Deliberately not done

- No caching decorator — admin-only, low-traffic (same call as Governance/User); the one cache is
  the badge count, inline, allowlisted.
- No cross-section FK constraints or nav properties — bare Guid columns, display data stitched
  in-service via `IUserServiceRead`/`ITeamServiceRead` (#996).
- No purge/delete of historical rows — the archive is the point; GDPR erasure is the only remover.
- No reporter-facing view and no un-retiring — new reporting is Issues' lane (#977).
- No `IFeedbackService` interface for the section's own controllers — they take the concrete
  service; only the two `Contracts/` interfaces leave the section.

## Load-bearing weirdness

- **Email-before-persist in `PostMessageAsync`** — deliberate ordering for retryability, not a bug.
- **`FeedbackSource` EF sentinel `(FeedbackSource)(-1)`** — out-of-range on purpose; the CLR
  default tripped EF's sentinel detection and silently dropped explicit `UserReport` assignments.
- **`AuditEntityTypes.FeedbackReport` is a persisted string literal** — a data contract with
  existing audit rows; never regenerate from `nameof`.
- **`FeedbackResource` must stay `namespace Humans.Feedback`** — the SDK derives the resource
  manifest name from it; moving it silently breaks every string. `Email_FeedbackResponse_*` keys
  deliberately live in the Email section's `EmailResource` (its `EmailRenderer` renders that email).
- **Singleton repository + `IDbContextFactory`** — repo owns context lifetime per call.
- **`RoleNames.FeedbackAdmin` still exists but grants no Feedback access** — kept because Staff
  page, Guide and `AnyAdminRole` still name it; its policy/role-group/check were deleted (#977).
- **`Source`/`AgentConversationId` are vestigial for new rows** but historical `AgentUnresolved`
  rows exist — readers must not assume `UserReport`.
- **Init-only `UserId`/`SenderUserId` mutated via `Entry().Property().CurrentValue`** in merge
  fold and erasure — bypassing the init setter is the intended EF idiom there.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-31 | Doc truth for the retired section, ModelState guards, GDPR/audit test pins | peterdrier/Humans#1566 |
