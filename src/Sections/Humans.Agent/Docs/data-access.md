# Agent — Data Access

## Agent

Project: `src/Sections/Humans.Agent` — services live under `Services/`
(with `Services/Anthropic/`, `Services/Preload/`, `Services/Stores/`
subfolders), repository under `Data/`. **DbContext:** `AgentDbContext`.
`AgentRepository` injects `AgentDbContext` directly (Scoped, not via
`IDbContextFactory` — mirrors `AgentService` being Scoped rather than
Singleton like most other sections' repositories). Owns
`AgentConversations`, `AgentMessages`, `AgentSettings`.

The preload/warmup surface: `AgentPreloadCorpusBuilder` assembles the
tool-corpus preload from `AgentSectionDocReader`, `AgentFeatureSpecReader`,
`CommunityFaqReader`, and calls into `IAgentPreloadAugmentor`
(`Humans.Agent.Services.Preload.AgentPreloadAugmentor`,
`src/Sections/Humans.Agent/Services/Preload/`) — it renders the access matrix / glossaries /
route map / FAQ preload pages from `Humans.Base`'s `AccessMatrixDefinitions` /
`SectionHelpContent` (every section's help content, visible from any section
since both live in the shared base layer). Pure static-content formatting — no DI
dependencies beyond the two static readers, no DB access, no cache.
`AgentPreloadWarmupHostedService` and `AgentSettingsStoreWarmupHostedService`
run startup warmup, no DB access — fan out over the readers /
`IAgentSettingsService` via `IServiceScopeFactory`. `AgentRateLimitStore`,
`AgentRetentionRunStore`, `AgentSettingsStore` are in-memory stores backing
the rate-limit / retention / settings caches — no DB access of their own.

The preload readers and the corpus builder cache their file-content reads in `IMemoryCache`
(no DB, `HoldForever`; the admin reload clears only `CommunityFaqReader`
and re-sets the corpus entries — the section/feature reader caches clear
only on process restart): `AgentSectionDocReader` (`agent:section:{key}`),
`AgentFeatureSpecReader` (`agent:feature:index`, `agent:feature:{stem}`),
`CommunityFaqReader` (`agent:community-kb:index`,
`agent:community-kb:doc:{stem}`), `AgentPreloadCorpusBuilder`
(`agent:preload:{config}`). `AgentPreloadAugmentor` itself is pure
static-content formatting — no cache, no DB.
`AgentToolDispatcher` also reads `IAuditViewerService`, `IShiftView`,
`IBurnSettingsService` for its tool surface.

### AgentService (Scoped, `Humans.Agent.Services`)

Repository: `IAgentRepository`.

| Table | R/W |
|-------|-----|
| AgentConversations | R/W |
| AgentMessages | R/W |
| AgentSettings | R (via `IAgentSettingsService`) |

Cross-section calls via `IAgentSettingsService`, `IAgentRateLimitStore`,
`IAgentAbuseDetector`, `IAgentUserSnapshotProvider`,
`IAgentPreloadCorpusBuilder`, `IAgentPromptAssembler`,
`IAgentToolDispatcher`, `IAnthropicClient`. Implements
`IUserDataContributor`, `IAgentTranscriptRead` (Backdoor's machine-API
transcript surface). Uses `AnthropicOptions`. No `IMemoryCache`.

### AgentAdminStatusService (Scoped, `Humans.Agent.Services`)

Repository: `IAgentRepository` (read-only window queries for the admin
status report).

| Table | R/W |
|-------|-----|
| AgentConversations | R |
| AgentMessages | R |

Cross-section calls via `IAgentSettingsService`, `IAgentRateLimitStore`,
`IAgentRetentionRunStore`, `IAgentAnthropicBalanceProvider`. Read-only
assembler for `/Agent/Admin/Status` — one 30-day projection, all
sub-windows computed in memory. No cache.

### AgentPricing

Static class — hard-coded per-1M-token Anthropic pricing for agent spend
estimates. No DI, no DB access.

### AgentSettingsService / AgentPromptAssembler / AgentToolDispatcher / AgentUserSnapshotProvider / AgentAbuseDetector

Live under `src/Sections/Humans.Agent/Services/`. The settings
service is the only one that touches `AgentSettings` directly (via
`AgentRepository.GetAgentSettingsAsync` / `UpsertAgentSettingsAsync`),
backed in-memory by `AgentSettingsStore`. The others are stateless
adapters or fan-out over public service interfaces (`ITeamServiceRead`,
`IUserServiceRead`, `IRoleAssignmentService`, `IConsentServiceRead`,
`IFeedbackServiceRead`, `ITicketServiceRead`, `IShiftView`,
`IBurnSettingsService`, `IAuditViewerService`, etc.) for the agent's
tool-dispatch and user-snapshot surfaces. No `IMemoryCache`.

### AnthropicClient (`Services/Anthropic/`)

Outbound API client over `AnthropicOptions`. No DB access, no cache.

### AnthropicBalanceProvider (`Services/Anthropic/`)

No repository. Reads the Anthropic credit balance over `AnthropicOptions`
(`GetBalanceAsync` → `AgentBalanceStatus`) for the admin status screen via
`AgentAdminStatusService`. No DB access, no cache.

### AgentDocsHealthCheck / AnthropicHealthCheck (`Health/`)

No repository, no DB access. `AgentDocsHealthCheck` probes GitHub doc
canaries directly through `IGuideContentSource` (deliberately bypassing the
readers' caches); `AnthropicHealthCheck` is a DNS-reachability probe for
`api.anthropic.com`. Both skip (Healthy) when the agent is disabled;
registered by `SectionHealthChecks`.

---


