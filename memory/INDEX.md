<!-- Loaded via CLAUDE.md. Keep tight: one line per atom. See META.md to maintain. -->

# Project Rules Index

Atomic rules. Fetch the body when the description's trigger matches your task. See [`META.md`](META.md) for the pattern; [`design-rules.md`](../docs/architecture/design-rules.md) for the architecture narrative.

---

## architecture/

- [`audit-log-as-concurrency-safety-net`](architecture/audit-log-as-concurrency-safety-net.md) — audit log catches admin-clobbers-admin races at this scale; don't reach for `IsConcurrencyToken` / row versioning
- [`caching-transparent`](architecture/caching-transparent.md) — no `Cached*` types in domain surface; `Full<Section>` is the §15 stitched-DTO pattern
- [`consent-record-immutable`](architecture/consent-record-immutable.md) — `consent_records` table: DB triggers block UPDATE/DELETE, INSERT only
- [`db-enforcement-minimal`](architecture/db-enforcement-minimal.md) — service is the contract, not the DB; only audit-log immutability is doctrinal
- [`interface-method-budget-ratchet`](architecture/interface-method-budget-ratchet.md) — HARD RULE. Add a method to a budgeted interface → remove one from the SAME interface, same PR. No splits to dodge.
- [`no-admin-url-section`](architecture/no-admin-url-section.md) — new admin pages live at `/<Section>/Admin/*`, never `/Admin/<Section>/*`
- [`no-column-drops-for-decoupling`](architecture/no-column-drops-for-decoupling.md) — HARD RULE. Property override IS the migration; column drop waits for a separate PR after prod verification
- [`no-concurrency-tokens`](architecture/no-concurrency-tokens.md) — HARD RULE. No `IsConcurrencyToken` / `[ConcurrencyCheck]` / row versioning. Single server, ~500 users.
- [`no-cross-section-ef-joins`](architecture/no-cross-section-ef-joins.md) — HARD RULE. A section's EF model joins only to its own tables. Cross-section linkage is a bare Guid column, never a `HasOne`/nav property/FK constraint.
- [`no-drops-until-prod-verified`](architecture/no-drops-until-prod-verified.md) — HARD RULE. Hard storage (DB columns/tables/indexes, files) drops in a separate PR after replacement is verified in prod
- [`no-hand-edited-migrations`](architecture/no-hand-edited-migrations.md) — HARD RULE. EF migrations 100% auto-generated. Backfills in admin buttons. Pre-commit hook enforces.
- [`no-linq-at-db-layer`](architecture/no-linq-at-db-layer.md) — services call thick repo methods returning materialized lists, not `db.Set<T>().Where/Select` chains
- [`no-startup-guards`](architecture/no-startup-guards.md) — HARD RULE. App must always boot. Fix at runtime / admin button / idempotent migration — never refuse to start.
- [`repository-required-for-db-access`](architecture/repository-required-for-db-access.md) — HARD RULE. Every DB-accessing service goes through a repository interface; no service injects `HumansDbContext` directly, even for singleton-row tables.
- [`shared-drives-only`](architecture/shared-drives-only.md) — Drive resources on Shared Drives only; API calls need `SupportsAllDrives` + `permissionDetails`
- [`user-profile-foundational`](architecture/user-profile-foundational.md) — UserService/ProfileService are bottom of the stack; no outbound calls to higher-level sections

## code/

- [`admin-role-superset`](code/admin-role-superset.md) — Admin = global superset; TeamsAdmin/CampAdmin/TicketAdmin = supersets in their domain. Always include both.
- [`always-log-problems`](code/always-log-problems.md) — log expected problems at LogWarning without exception object; LogInformation is invisible in prod
- [`authorization-conventions`](code/authorization-conventions.md) — `[Authorize(Roles = ...)]` with `RoleGroups`/`RoleNames`; no inline `IsInRole` chains
- [`controller-base-conventions`](code/controller-base-conventions.md) — inherit `HumansControllerBase`; use `GetCurrentUserAsync`/`SetSuccess`/`SetError`. No raw `_userManager` or `TempData["..."]`.
- [`csv-and-pagination-helpers`](code/csv-and-pagination-helpers.md) — use `AppendCsvRow`/`ToCsvField` and `ClampPageSize()` instead of inline equivalents
- [`culture-and-language`](code/culture-and-language.md) — use `CultureCatalog`/`CultureCodeExtensions`; no per-view language dictionaries
- [`datetime-display-formatting`](code/datetime-display-formatting.md) — use `ToDisplayDate`/`ToDisplayDateTime`/`ToAuditTimestamp`; no inline format strings
- [`icons-fa6-only`](code/icons-fa6-only.md) — `fa-solid fa-*`; never `bi bi-*` (Bootstrap Icons not loaded → invisible)
- [`json-serialization`](code/json-serialization.md) — System.Text.Json: private setters need `[JsonInclude]`; new classes need `[JsonConstructor]`; polymorphic types need `[JsonPolymorphic]` + `[JsonDerivedType]`
- [`localization-admin-exempt`](code/localization-admin-exempt.md) — admin pages don't need localization; no new `@Localizer[...]` keys for `/Admin/*`
- [`log-file-debugging`](code/log-file-debugging.md) — Grep the log before speculating; write diagnostic logs with entity IDs and actual values
- [`lsp-integration`](code/lsp-integration.md) — re-Read each `.cs` after editing; LSP diagnostics fire on Read, not Edit
- [`namespace-alias-application`](code/namespace-alias-application.md) — `using MemberApplication = Humans.Domain.Entities.Application;` (namespace collision)
- [`no-enum-compare-in-ef`](code/no-enum-compare-in-ef.md) — enums with `HasConversion<string>()` translate to lexicographic SQL; use `Contains()` with explicit allowed-values list
- [`no-extensions-for-owned-classes`](code/no-extensions-for-owned-classes.md) — methods/properties go on owned classes; extensions only for BCL/NuGet types
- [`no-hallucinated-content`](code/no-hallucinated-content.md) — never hardcode invented copy (benefits, policies, pricing); wire to admin-editable fields or ask
- [`no-magic-strings`](code/no-magic-strings.md) — `nameof()`/constants/enums for code-identifier strings (`RedirectToAction`, role names, audit entity types)
- [`no-remove-unused-properties`](code/no-remove-unused-properties.md) — properties may be reflection-bound (serialization, change tracking); verify before removing
- [`no-rename-serialized-fields`](code/no-rename-serialized-fields.md) — never rename properties on JSON-serialized classes; existing data expects current names
- [`no-system-subfolder`](code/no-system-subfolder.md) — never create `System/` subfolder; shadows BCL `System`. Use `SystemSettings/`/`Platform/`/`Infra/`.
- [`nodatime-for-dates`](code/nodatime-for-dates.md) — `Instant`/`LocalDate`/`ZonedDateTime` not `DateTime`; server-side ALWAYS UTC
- [`profiles-section-plural`](code/profiles-section-plural.md) — `Humans.*.Services.Profiles` (plural); singular collides with the `Profile` entity
- [`sanitized-markdown-rendering`](code/sanitized-markdown-rendering.md) — `@Html.SanitizedMarkdown(...)`; no inline `HtmlSanitizer`/`Markdig.Markdown.ToHtml`
- [`search-endpoint-response-shape`](code/search-endpoint-response-shape.md) — search/autocomplete endpoints return typed DTOs/records, not anonymous objects
- [`string-comparisons-explicit`](code/string-comparisons-explicit.md) — `StringComparison.Ordinal`/`OrdinalIgnoreCase`; user search uses shared `Humans.Web.Extensions` helpers
- [`time-parsing-standardization`](code/time-parsing-standardization.md) — `TryParseInvariantTimeOnly`/`TryParseInvariantLocalTime` from `TimeParsingExtensions`
- [`update-source-attribution`](code/update-source-attribution.md) — `CommunicationPreference.UpdateSource` must reflect actor (signed-in/anon) + channel; don't conflate `Guest` (session) with `MagicLink` (token)
- [`view-components-vs-partials`](code/view-components-vs-partials.md) — View Component when it fetches its own data; Partial View when parent already has the model
- [`viewcomponent-no-cache`](code/viewcomponent-no-cache.md) — view components must NOT inject `IMemoryCache`; the owning service exposes a cached accessor

## process/

- [`about-page-license-attribution`](process/about-page-license-attribution.md) — after any NuGet update, add new versions + licenses to `Views/About/Index.cshtml`
- [`after-prod-merge-reset`](process/after-prod-merge-reset.md) — after upstream PR lands: `git fetch upstream && git reset --hard upstream/main && git push origin main --force-with-lease`
- [`discord-release-notes-format`](process/discord-release-notes-format.md) — audience-grouped (coordinators/volunteers/under-the-hood/known-issues), plain-language, no emojis
- [`dotnet-verbosity-quiet`](process/dotnet-verbosity-quiet.md) — always `-v quiet` on `dotnet build`/`test`; never pipe through `tail`/`head`/`grep`
- [`drive-by-fixes-ok`](process/drive-by-fixes-ok.md) — small unrelated fixes can land in the same PR ONLY after Peter explicitly approves; surface and ask, never bundle silently
- [`ef-migration-review-gate`](process/ef-migration-review-gate.md) — MANDATORY. Run `.claude/agents/ef-migration-reviewer.md` before commit/PR
- [`feature-spec-on-new-feature`](process/feature-spec-on-new-feature.md) — when implementing a non-trivial new feature, create `docs/features/<feature>.md` in the same PR (covers create-new; post-fix-doc-check covers update-existing)
- [`issue-comments-mandatory`](process/issue-comments-mandatory.md) — HARD RULE (hook). Always fetch issues/PRs with comments; Peter's comments often flip OP intent
- [`issue-no-non-peter-without-approval`](process/issue-no-non-peter-without-approval.md) — HARD RULE (hook). If `.author.login != peterdrier`, STOP and get Peter's input first
- [`issue-refs-qualified`](process/issue-refs-qualified.md) — `peterdrier#N` (fork) or `nobodies-collective#N` (upstream); pass `--repo` to every `gh` call
- [`maintenance-log-update`](process/maintenance-log-update.md) — after any recurring maintenance, update `docs/architecture/maintenance-log.md` with current + next-due dates
- [`no-anon-perf-guards`](process/no-anon-perf-guards.md) — don't flag cheap `[AllowAnonymous]` DB reads as perf issues; auth guard is dead defensive code at this scale
- [`no-direct-to-main`](process/no-direct-to-main.md) — HARD RULE. Feature branch + PR for code/docs/config; `memory/**`-only changes either bundle with the discovery PR or go direct to `origin/main` standalone
- [`post-fix-doc-check`](process/post-fix-doc-check.md) — before final commit, scan `docs/features/` and `docs/sections/` for invariants the change touches; update inline
- [`pr-codex-thread-replies`](process/pr-codex-thread-replies.md) — reply per Codex inline thread (`POST /pulls/{n}/comments/{id}/replies`), not as top-level PR comment
- [`pr-done-means-codex-clean`](process/pr-done-means-codex-clean.md) — a PR isn't "done" until Codex returns no findings; pushed+green is mid-state
- [`pr-no-ping-reviewers`](process/pr-no-ping-reviewers.md) — don't `@codex review` after pushes; quota is limited, Claude reviews on push automatically
- [`pr-review-both-repos`](process/pr-review-both-repos.md) — pull comments from BOTH `peterdrier/Humans` AND `nobodies-collective/Humans`; use `/pulls/{n}/comments` for inline
- [`privilege-changes-need-explicit-approval`](process/privilege-changes-need-explicit-approval.md) — HARD RULE. Any change granting users new/elevated capability (Drive role bumps, auth-scope additions, role grants, admin flags, default permission tiers, CORS/allowlist expansions) needs Peter's explicit per-change approval before implementation, regardless of issue tier or sprint plan
- [`rules-maintenance`](process/rules-maintenance.md) — when a new project rule surfaces, capture as `memory/<bucket>/<name>.md` + INDEX entry in the same commit. Not external memory.
- [`simplify-scope-to-section-size`](process/simplify-scope-to-section-size.md) — scale `/simplify` fix counts to section LOC, not to a smaller prior PR's count
- [`todos-and-issue-tracking`](process/todos-and-issue-tracking.md) — after resolving commits: update `todos.md` Completed + close GitHub issues with summary + SHA
- [`triage-fetch-full-history`](process/triage-fetch-full-history.md) — `/triage` must `GET /api/feedback/{id}/messages` for every report; list-endpoint counts can be stale
- [`triage-show-verbatim`](process/triage-show-verbatim.md) — `/triage` always shows reporter's verbatim Description text alongside the analysis
- [`user-feedback-spec-changes-need-review`](process/user-feedback-spec-changes-need-review.md) — when an issue originated from end-user feedback (triage→issue chain) and proposes a behavioral / policy / capability / spec change beyond a mechanical fix, route to Peter for review before sprint/batch dispatch. Mechanical fixes (typos, broken links, error-message wording) flow normally.
- [`worktree-removal-git-only`](process/worktree-removal-git-only.md) — HARD RULE. Worktree cleanup is `git worktree remove` only. Failure → report and stop. Narrow exception: if git emptied contents but left an empty parent dir, `rmdir` (non-recursive) is allowed. Otherwise no PowerShell `Remove-Item -Recurse`, no rm -rf, no process kills, no retries.

## product/

- [`birthday-not-dob`](product/birthday-not-dob.md) — store birthday (month + day only); UI says "birthday", never "date of birth"
- [`coolify-build-constraint`](product/coolify-build-constraint.md) — Coolify strips `.git`; never `COPY .git` in Dockerfile; use `SOURCE_COMMIT` build arg
- [`humans-terminology`](product/humans-terminology.md) — UI uses "humans"; never "members"/"volunteers"/"users". Stays English in es/de/fr/it.
- [`no-event-name-nowhere`](product/no-event-name-nowhere.md) — never use "Nowhere" in user-facing text (legal); "Elsewhere" is the current event name
- [`no-url-aliases`](product/no-url-aliases.md) — single canonical URL per page; only sanctioned alias is Barrios↔Camps
- [`profile-visibility-acceptable`](product/profile-visibility-acceptable.md) — basic profile info visible to other authenticated users (incl. suspended/unapproved) is intentional, not a security finding
- [`vol-being-removed`](product/vol-being-removed.md) — TRANSITIONAL. `/Vol/*` is being removed; don't extend new UX or flag inconsistency with `/Shifts`
- [`voting-not-prominent`](product/voting-not-prominent.md) — Voting/Review/Applications serve ~8 people; don't headline. Default order = daily-traffic-across-the-whole-audience.
