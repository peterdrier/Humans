# Backdoor — Data Access

## Backdoor

Project: `src/Sections/Humans.Backdoor` — service under `Services/`,
repository under `Data/`. **DbContext:** `BackdoorDbContext`.
`BackdoorApiKeyRepository` injects `IDbContextFactory<BackdoorDbContext>`
directly (Singleton, §15b). Owns `backdoor_api_keys`.

### BackdoorApiKeyService (Scoped)

Repository: `IBackdoorApiKeyRepository`.

| Table | R/W |
|-------|-----|
| backdoor_api_keys | R/W |

No `IMemoryCache` — a cache would have to be invalidated on every revoke to
stay correct about the thing that matters most, and key lookups are one
indexed hash probe per API request at a handful of requests per minute.

Cross-section calls via `IRoleAssignmentService` (Admin/Board eligibility),
`IUserServiceRead` (account-state check, display-name stitching),
`IAuditLogService` (key lifecycle). Implements `IUserDataContributor`
(GDPR export slice `GdprExportSections.BackdoorApiKeys`, hash excluded),
`IUserMerge` (folds an eliminated account's keys onto the survivor).

The four `/api/backdoor/*` machine APIs (`agent`, `issues`, `feedback`,
`surveys`) are thin controller orchestrators over another section's
contracts interface (`IAgentTranscriptRead`, `IIssueTriage`,
`IFeedbackTriage`, `ISurveyAnalysisRead`) — Backdoor's own repository is
never touched by them, and they carry no `###` heading here since each is
documented in the section that owns the interface.

---

