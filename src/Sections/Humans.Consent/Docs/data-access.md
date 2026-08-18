# Consent — Data Access

## Legal

Folder: `src/Sections/Humans.Consent/Services/` — Legal and
[Consent](#consent) **share one project**, `src/Sections/Humans.Consent/`
(they always shared `LegalDbContext`). **DbContext:**
`LegalDbContext`. `LegalDocumentRepository` injects
`IDbContextFactory<LegalDbContext>` directly. Owns `LegalDocuments`,
`DocumentVersions` (`ConsentRecords`, the third table on this context, is
owned by the same section's [Consent](#consent) repository below). The inner `ILegalDocumentSyncService` is wrapped by
`Humans.Consent.Services.CachingLegalDocumentSyncService`
(Singleton decorator inheriting `TrackedCache<Guid, LegalDocumentInfo>`,
warmed on startup, with a version-id → document-id index). It caches the
global active-document set behind the every-page consent-banner read and
the per-version lookup, invalidated wholesale after any persisted
`legal_documents` / `document_versions` write.

`LegalDocumentSyncService` is the sole writer for `LegalDocuments` /
`DocumentVersions` — it implements both `ILegalDocumentSyncService` (the
GitHub-sync write/read surface) and `IAdminLegalDocumentService` (the
admin create/update/archive/version-summary surface). Being the single
writer lets it call `ILegalDocumentCacheInvalidator.InvalidateAll()`
directly after each successful repository write instead of relying on a
cross-cutting EF `SaveChangesInterceptor`.

### LegalDocumentService (Scoped)

No repository (read-through service). Uses `IGitHubLegalDocumentConnector`
+ `IMemoryCache`.

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `Legal:{slug}` | 1 hr | yes | yes | yes |

No DB access. Documents are cached from the GitHub source. Unrelated to
`LegalDocumentSyncService` below — this is the public `/Legal` Statutes
content provider (no DB access); the naming collision exists because the
merged writer took the `LegalDocumentSyncService` name since
`LegalDocumentService` was already taken by this class.

### LegalDocumentSyncService (Scoped — wrapped by CachingLegalDocumentSyncService Singleton decorator; implements `ILegalDocumentSyncService` + `IAdminLegalDocumentService`)

Repository: `ILegalDocumentRepository`.

| Table | R/W |
|-------|-----|
| LegalDocuments | R/W |
| DocumentVersions | R/W |

Sole writer for both the GitHub-sync surface (`SyncDocumentAsync` /
`SyncAllDocumentsAsync` — version add, sync-touch) and the admin surface
(`CreateLegalDocumentAsync` / `UpdateLegalDocumentAsync` /
`ArchiveLegalDocumentAsync` / `UpdateVersionSummaryAsync`). Calls
`ILegalDocumentCacheInvalidator.InvalidateAll()` directly after each
successful write. Cross-section calls via `INotificationEmitter`,
`ITeamService` (full service — team-name stitching for the admin list and
the active-required-by-team read), `IUserServiceRead` (active-user
fan-out for re-consent-required notifications), `IGitHubLegalDocumentConnector`,
plus `IOptions<GitHubSettings>`. The inner service has no `IMemoryCache`;
caching lives in the decorator. Periodic background sync of legal
documents from the legal-internal repo.

### LegalDocumentSyncRunner (Scoped)

Repository: `IConsentRepository`.

| Table | R/W |
|-------|-----|
| ConsentRecords | R |

The body of `SyncLegalDocumentsJob`. Runs
`ILegalDocumentSyncService.SyncAllDocumentsAsync`, then, for each updated
document, narrows the affected teams' active members down to those without a
consent record for the new version — `GetPairsForUsersAndVersionsAsync` is its
only repository call, read-only — and mails them the re-consent notice.
Cross-section calls via `IEmailService` and `IEmailMessageFactory`
(`ReConsentsRequired`), `ITeamServiceRead` (team membership) and
`IUserServiceRead`. No cache.

### CachingLegalDocumentSyncService (Singleton, `Humans.Consent.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, LegalDocumentInfo>` (`Legal.LegalDocumentInfo`, warmed on startup, + version-id index) | Per-Entity | yes | yes (warm/load) | yes (wholesale via `ILegalDocumentCacheInvalidator.InvalidateAll`, called directly by `LegalDocumentSyncService` after each successful write) |

Implements `ILegalDocumentSyncService`, `ILegalDocumentCacheInvalidator`.
Warm resolves team display names via `ITeamService` through a fresh DI
scope per warm. Surfaced on `/Debug/CacheStats`.

---


## Consent

Folder: `src/Sections/Humans.Consent/Services/` — shares the project with
[Legal](#legal) above (see that section's header for why). **DbContext:**
`LegalDbContext` — Consent has no dedicated context of its own, it rides on
the same context as Legal's `LegalDocuments` /
`DocumentVersions`. `ConsentRepository` injects
`IDbContextFactory<LegalDbContext>` directly. Owns `ConsentRecords`.
The inner `IConsentService` is wrapped by
`Humans.Consent.Services.CachingConsentService` (Singleton
decorator inheriting `TrackedCache<Guid, UserConsentInfo>`, lazy / no
startup warmup). It caches the per-user set of consented document-version
ids (with the account-merge source-id chain resolved at load) and
**synchronously** evicts the affected user on `SubmitConsentAsync` before
returning, so the next-page consent-banner check never observes a stale
"still required" entry. It exposes the cross-section read surface as
`IConsentServiceRead`.

### ConsentService (Scoped — wrapped by CachingConsentService Singleton decorator)

Repository: `IConsentRepository`.

| Table | R/W |
|-------|-----|
| ConsentRecords | R/W |

Cross-section calls via `ILegalDocumentSyncService`,
`INotificationInboxService`, `ISystemTeamSync`, `IUserServiceRead`,
`IHumansMetrics`, plus `IServiceProvider` for cycle-breaking. Implements
`IUserDataContributor`. The inner service has no `IMemoryCache`; caching
lives in the decorator.

### CachingConsentService (Singleton, `Humans.Consent.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, UserConsentInfo>` (`Consent.UserConsentInfo`, lazy, no warmup) | Per-User | yes (consented-version-set reads) | yes (lazy load) | yes (synchronous per-user evict on submit; via `IConsentCacheInvalidator`) |

Implements `IConsentService`, `IConsentServiceRead`,
`IConsentCacheInvalidator`. Richer record reads (dashboards, history,
record counts) pass through to the inner service. Surfaced on
`/Debug/CacheStats`.

---


