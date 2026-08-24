<!-- freshness:triggers
  src/Sections/Humans.Backdoor/**
-->
<!-- freshness:flag-on-change
  Re-read the surface table and the auth model whenever a controller, the key service or the auth filter changes: the routes are a published contract for agents, and the "one key = one human" rule is the whole point of the section.
-->

# Backdoor — Section Invariants

The machine surface. Every key-authed API an agent talks to lives here, under `/api/backdoor/*`, gated by one personal key per human.

## Concepts

- A **Backdoor API key** is a credential issued to a *person*, not a service. Its plaintext exists only at the moment of issue; the database keeps a SHA-256 hash and a 12-character display prefix.
- **Issue / rotate / revoke** are the whole lifecycle. There is no "read the key back" — a lost key is rotated.
- The **machine surface** is the five read/write APIs Backdoor owns. Each is a thin orchestrator over another section's public contracts interface; Backdoor owns no domain data beyond its keys.

## Data Model

### BackdoorApiKey

**Table:** `backdoor_api_keys`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | The human the key belongs to; every request authenticates as them |
| KeyHash | string(64) | SHA-256 of the plaintext, lowercase hex. Unique |
| DisplayPrefix | string(16) | First 12 characters of the plaintext, so a human can tell their rows apart |
| Label | string(100) | Free text — what the key is for |
| CreatedAt | Instant | |
| CreatedByUserId | Guid | The admin who allocated it |
| LastUsedAt | Instant? | Stamped on every successful resolve |
| RevokedAt | Instant? | Null means active |
| RevokedByUserId | Guid? | |

**Indexes / constraints:** unique on `KeyHash` (a presented key must resolve to at most one row); non-unique on `UserId`.

**Cross-section FKs:** `UserId`, `CreatedByUserId`, `RevokedByUserId` → `Users.User` (Users), as bare Guids — the Identity tables stay in `UsersDbContext`.

## Routing

| Route | Access | Serves |
|-------|--------|--------|
| `/api/backdoor/logs` | read | The in-memory log ring (`InMemoryLogSink`, Base) |
| `/api/backdoor/agent` | read | Agent conversation transcripts, via `IAgentTranscriptRead` |
| `/api/backdoor/issues` | read + write | The issue queue, via `IIssueTriage` |
| `/api/backdoor/feedback` | read + write | The feedback queue, via `IFeedbackTriage` |
| `/api/backdoor/surveys` | read | Survey definitions, responses and aggregates, via `ISurveyAnalysisRead` |
| `/Backdoor` | Admin UI | Allocate, rotate and revoke keys |

Authentication is the `X-Api-Key` header on every `/api/backdoor/*` request. There is no cookie path in and no anonymous endpoint.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Holder of an active key | Everything the five APIs expose, acting as themselves |
| Board member | May be issued a key |
| Admin | All Board capabilities. Additionally: allocate, rotate and revoke anyone's key from `/Backdoor` |

## Invariants

- A key resolves to exactly one human, and that human is installed as the request principal (`ClaimTypes.NameIdentifier`), so every write records a real actor and every log line is enriched with them.
- The database never holds a plaintext key. `BackdoorApiKeyService` hashes on the way in and compares hashes on the way out.
- A key only works for a full Admin or a Board member **whose account state is `Active`** — checked at issue, at rotation, **and on every authentication**. A role that expires, is revoked, or is swept by account deletion stops the key working on the next request, and so does suspension, which moves `users.State` while deliberately leaving role assignments standing. The row is refused, not revoked, so restoring the role or lifting the suspension restores the key. The admin page shows such a key as **Disabled** and withholds Rotate, since rotation applies the same test.
- Issue and revoke both write an audit entry naming the key and its owner (`BackdoorApiKeyIssued` / `BackdoorApiKeyRevoked`); a rotation is recorded as a revoke followed by an issue.
- Every controller here is an orchestrator: it calls another section's contracts interface and formats the result. None of them touch a repository or a `DbContext` other than through `IBackdoorApiKeyRepository`.
- A key-authed principal carries the `BackdoorApiKey` authentication scheme (`BackdoorAuthentication.SchemeName`). It never passes through the Shell's claims transformation, so it carries no role or state claims — and the Shell's onboarding gates (`NameRequiredFilter`, `MembershipRequiredFilter`) skip it rather than redirecting a JSON client to an HTML page.

## Negative Access Rules

- A caller with no `X-Api-Key` header **cannot** reach any endpoint — 401.
- A caller with an unknown or revoked key **cannot** reach any endpoint — 401, deliberately indistinguishable from the above. There is no "server not configured" status: keys are rows, not environment variables.
- An admin **cannot** recover a key's plaintext after issue, their own included.
- A former Admin or Board member **cannot** keep using a key issued while they held the role — 401 from the next request on.
- A suspended Admin or Board member **cannot** use their key: the machine surface is exempt from the onboarding gates the account-status wall runs through, so eligibility is the only thing standing between a suspended account and the API.
- A plain member or volunteer **cannot** be issued a key, however the request is made.
- Backdoor **cannot** read another section's tables. Anything it serves comes from that section's published contracts interface.

## Triggers

- On issue: an audit entry (`BackdoorApiKeyIssued`), and the plaintext is surfaced once through TempData to the admin page.
- On revoke: an audit entry (`BackdoorApiKeyRevoked`) and `RevokedAt`/`RevokedByUserId` stamped.
- On rotate: revoke of the old key, then issue of a replacement carrying the same owner and label — two audit entries.
- On every successful authentication: `LastUsedAt` stamped on the key. A key whose owner is no longer eligible authenticates nothing and is not stamped.
- On GDPR erasure: the human's keys are hard-deleted, and they are detached as the creator or revoker of anyone else's — the row belongs to its owner, not to the admin who handled it.
- On account merge: the eliminated account's keys, and its actor columns, fold onto the survivor.

## Cross-Section Dependencies

- **Agent**: reads transcripts via `IAgentTranscriptRead` (`Humans.Agent.Contracts`).
- **Feedback**: reads and triages via `IFeedbackTriage` (`Humans.Feedback.Contracts`).
- **Issues**: reads and triages via `IIssueTriage` (`Humans.Issues.Contracts`).
- **Surveys**: reads definitions, exports and aggregates via `ISurveyAnalysisRead` (`Humans.Surveys.Contracts`).
- **Auth**: `IRoleAssignmentService.IsUserAdminAsync` / `IsUserBoardMemberAsync` for key eligibility, and `GetActiveUserIdsInRoleAsync` for the admin page's recipient list — narrowed there to active accounts, so the dropdown never offers someone the service would refuse and each listed key shows whether it still authenticates.
- **AuditLog**: `IAuditLogService.LogAsync` for the key lifecycle.
- **Gdpr**: `IUserDataContributor` — `backdoor_api_keys` is user-keyed, so the section owes an Article 15 slice (`GdprExportSections.BackdoorApiKeys`, hash excluded) and an Article 17 erasure.
- **Users**: `IUserServiceRead.GetUserInfoAsync` for the account-state half of key eligibility and `GetUserInfosAsync` for display names on the admin page and on the API's issue/feedback projections; `IUserMerge` to fold an eliminated account's keys onto the survivor.
- **Base**: `InMemoryLogSink` behind `/api/backdoor/logs`.

No section depends on Backdoor. It is a leaf, and deliberately so — the fan-in would otherwise be a cycle. The Shell reads one constant from it, `BackdoorAuthentication.SchemeName`, so its onboarding gates can tell a machine request from a browsing session.

## Architecture

**Owning services:** `BackdoorApiKeyService`
**Owned tables:** `backdoor_api_keys`
**Status:** (A) Migrated — created at this shape in nobodies-collective/Humans#1128 (2026-08).

### Cross-section read interface

None. No other section consumes Backdoor; its whole surface is HTTP, and its service interface is section-internal.

| Read interface | Methods | Notes |
|---|---:|---|
| — | — | Not cross-section-consumed |

- `BackdoorApiKeyService` never imports `Microsoft.EntityFrameworkCore`; `IBackdoorApiKeyRepository` (Singleton + `IDbContextFactory`, §15b) is the only path to the table.
- **Decorator decision** — no caching decorator. Key lookups are one indexed hash probe per API request at a handful of requests per minute, and a cache would have to be invalidated on every revoke to stay correct about the thing that matters most.
- **Display stitching** — `IUserServiceRead.GetUserInfosAsync`.
