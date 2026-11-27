# Backdoor — Data Access

## Backdoor

Project: `src/Sections/Humans.Backdoor` — service under `Services/`,
repository under `Data/`. **DbContext:** `BackdoorDbContext`.
`BackdoorApiKeyRepository` injects `IDbContextFactory<BackdoorDbContext>`
directly (Singleton, per design-rules.md, *Section caching pattern*). Owns `backdoor_api_keys`.
`BackdoorOAuthRepository` likewise owns `backdoor_oauth_connections` and `backdoor_oauth_tokens`.

### BackdoorApiKeyService (Scoped)

Repository: `IBackdoorApiKeyRepository`.

| Table | R/W |
|-------|-----|
| backdoor_api_keys | R/W |

No `IMemoryCache` — a cache would have to be invalidated on every revoke to
stay correct about the thing that matters most, and key lookups are one
indexed hash probe per API request at a handful of requests per minute.

Cross-section calls via `ISystemRoleService` (Admin/Board eligibility),
`IUserService` (account-state check, display-name stitching),
`IAuditLogService` (key lifecycle). Implements `IUserDataContributor`
(GDPR export slice `BackdoorApiKeyService.BackdoorApiKeys`, hash excluded),
`IUserMerge` (folds an eliminated account's keys onto the survivor).

### BackdoorConnectionService (Scoped)

Repository: `IBackdoorOAuthRepository`, directly and through OpenIddict's managers, which call
the Backdoor stores (`BackdoorOAuthConnectionStore`, `BackdoorOAuthTokenStore`, registered Scoped).
OpenIddict's applications come from code (`BackdoorOAuthClientStore`, no table).

| Table | R/W |
|-------|-----|
| backdoor_oauth_connections | R/W |
| backdoor_oauth_tokens | R/W |

No cache: OpenIddict checks the stored token and connection on every MCP call so a revoke stops
it at once. Cross-section calls via `IAuditLogService` (connection lifecycle) and
`INotificationEmitter` (approval notice). Implements `IUserDataContributor` (slice
`BackdoorConnections`; erasure deletes) and `IUserMerge` (revokes the eliminated account's
connections). `BackdoorOAuthPruneJob` prunes through the same stores daily.

The `/api/backdoor/*` machine APIs that serve another section (`agent`,
`issues`, `store`, `surveys`, `finance`, `notifications`, `tasks`) are thin controller orchestrators over that
section's contracts interface (`IAgentTranscriptRead`, `IIssueTriage`,
`IStoreAccountingRead`, `ISurveyAnalysisRead`, `ITaskWrite`,
`IExpenseReportService`, `IFinanceService`, `IBudgetService`, `INotificationInboxRead`) — Backdoor's own repository is
never touched by them, and they carry no `###` heading here since each is
documented in the section that owns the interface. `logs` alone reads
`InMemoryLogSink` from `Humans.Base` and owns no data either.

---

