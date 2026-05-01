<!-- This file is loaded into Claude's context via CLAUDE.md. Keep it tight: one line per atom, descriptive enough that future-you knows when to fetch the body. See META.md for the pattern. -->

# Project Rules Index

Atomic project rules. Each link is a single rule with `Why:` and `How to apply:`. Read the body when the description's trigger matches what you're doing.

See [`META.md`](META.md) for: bucket conventions, file format, and how to add new rules.

For narrative architecture (layer responsibilities, table ownership, §15 caching story), see [`docs/architecture/design-rules.md`](../docs/architecture/design-rules.md) — that's the constitution; this index is the case law.

---

## architecture/

System-level rules about how the code is shaped.

- [`audit-log-as-concurrency-safety-net`](architecture/audit-log-as-concurrency-safety-net.md) — at 500-user scale, audit log makes lost-update races tolerable; don't reach for `IsConcurrencyToken` / row versioning
- [`caching-transparent`](architecture/caching-transparent.md) — never introduce `Cached*` types in domain surface; `Full<Section>` is the §15 stitched-DTO pattern
- [`consent-record-immutable`](architecture/consent-record-immutable.md) — `consent_records` table has DB triggers blocking UPDATE/DELETE; INSERT only
- [`db-enforcement-minimal`](architecture/db-enforcement-minimal.md) — service is the contract, not the DB; only audit-log immutability is doctrinal
- [`interface-method-budget-ratchet`](architecture/interface-method-budget-ratchet.md) — HARD RULE. Adding a method to a budgeted interface requires removing one from the SAME interface in the SAME PR
- [`no-admin-url-section`](architecture/no-admin-url-section.md) — new admin pages live at `/<Section>/Admin/*`, never `/Admin/<Section>/*`
- [`no-column-drops-for-decoupling`](architecture/no-column-drops-for-decoupling.md) — HARD RULE. Property override IS the migration; column drops wait for a separate PR after prod verification
- [`no-concurrency-tokens`](architecture/no-concurrency-tokens.md) — HARD RULE. No `IsConcurrencyToken` / `[ConcurrencyCheck]` / row versioning. Single server, ~500 users
- [`no-drops-until-prod-verified`](architecture/no-drops-until-prod-verified.md) — HARD RULE. DB columns/tables/indexes/persisted state — drop in a separate PR after replacement is verified in production
- [`no-hand-edited-migrations`](architecture/no-hand-edited-migrations.md) — HARD RULE. EF migrations must be 100% auto-generated; pre-commit hook enforces. Backfills go in admin buttons
- [`no-linq-at-db-layer`](architecture/no-linq-at-db-layer.md) — services call thick repo methods returning materialized lists, not `db.Set<T>().Where/Select` chains
- [`no-startup-guards`](architecture/no-startup-guards.md) — HARD RULE. App must always boot. Fix at runtime, via admin button, or idempotent migration step
- [`shared-drives-only`](architecture/shared-drives-only.md) — all Google Drive resources on Shared Drives; API calls require `SupportsAllDrives = true` and `permissionDetails`
- [`user-profile-foundational`](architecture/user-profile-foundational.md) — UserService and ProfileService sit at the bottom of the stack; no outbound calls to higher-level sections

## code/

Code-level conventions and patterns.

- [`admin-role-superset`](code/admin-role-superset.md) — Admin can do everything; TeamsAdmin/CampAdmin/TicketAdmin are supersets within their domain. Always include both in role lists
- [`always-log-problems`](code/always-log-problems.md) — log expected problems at LogWarning without exception object; LogInformation is invisible in prod
- [`authorization-conventions`](code/authorization-conventions.md) — `[Authorize(Roles = ...)]` with `RoleGroups`/`RoleNames`; no inline `IsInRole` chains
- [`controller-base-conventions`](code/controller-base-conventions.md) — inherit `HumansControllerBase`; use `GetCurrentUserAsync` / `SetSuccess` / `SetError` helpers; no direct `_userManager.GetUserAsync` or `TempData["..."]`
- [`csv-and-pagination-helpers`](code/csv-and-pagination-helpers.md) — use `AppendCsvRow`/`ToCsvField` and `ClampPageSize()` instead of inline equivalents
- [`culture-and-language`](code/culture-and-language.md) — use `CultureCatalog`/`CultureCodeExtensions`; no per-view language dictionaries
- [`datetime-display-formatting`](code/datetime-display-formatting.md) — use `ToDisplayDate`/`ToDisplayDateTime`/`ToAuditTimestamp`/etc.; no inline `ToString("d MMM yyyy")` format strings
- [`icons-fa6-only`](code/icons-fa6-only.md) — use `fa-solid fa-*`; never `bi bi-*` (Bootstrap Icons not loaded → invisible)
- [`json-serialization`](code/json-serialization.md) — System.Text.Json: private setters need `[JsonInclude]`; new classes need `[JsonConstructor]`; polymorphic types need `[JsonPolymorphic]` + `[JsonDerivedType]`
- [`localization-admin-exempt`](code/localization-admin-exempt.md) — admin pages do not require localization; don't add new `@Localizer[...]` calls or resource keys for `/Admin/*`
- [`log-file-debugging`](code/log-file-debugging.md) — Grep the log file before speculating about runtime errors; write diagnostic logs with entity IDs and actual values
- [`lsp-integration`](code/lsp-integration.md) — re-Read each `.cs` file after editing it; LSP diagnostics fire on Read, not Edit
- [`namespace-alias-application`](code/namespace-alias-application.md) — use `using MemberApplication = Humans.Domain.Entities.Application;` due to namespace collision
- [`no-enum-compare-in-ef`](code/no-enum-compare-in-ef.md) — enums with `HasConversion<string>()` use lexicographic SQL comparison; use `Contains()` with explicit allowed-values list
- [`no-extensions-for-owned-classes`](code/no-extensions-for-owned-classes.md) — add methods/properties directly on owned classes; extensions only for BCL/NuGet types
- [`no-hallucinated-content`](code/no-hallucinated-content.md) — never hardcode invented copy (benefits, policies, pricing, vendor lists); wire to admin-editable fields or ask
- [`no-magic-strings`](code/no-magic-strings.md) — use `nameof()` / constants / enum references for code-identifier strings (`RedirectToAction`, role names, audit entity types)
- [`no-remove-unused-properties`](code/no-remove-unused-properties.md) — properties may be reflection-bound (serialization, change tracking, dynamic binding); verify before removing
- [`no-rename-serialized-fields`](code/no-rename-serialized-fields.md) — never rename properties on JSON-serialized classes; existing stored data expects current names
- [`no-system-subfolder`](code/no-system-subfolder.md) — never create a `System/` subfolder; shadows BCL `System` namespace across siblings. Use `SystemSettings/`/`Platform/`/`Infra/`
- [`nodatime-for-dates`](code/nodatime-for-dates.md) — use `Instant`/`LocalDate`/`ZonedDateTime` instead of `DateTime`; server-side ALWAYS UTC
- [`profiles-section-plural`](code/profiles-section-plural.md) — `Humans.*.Services.Profiles` (plural); singular `Profile` collides with the `Profile` entity class
- [`sanitized-markdown-rendering`](code/sanitized-markdown-rendering.md) — use `@Html.SanitizedMarkdown(...)`; no inline `HtmlSanitizer` / `Markdig.Markdown.ToHtml`
- [`search-endpoint-response-shape`](code/search-endpoint-response-shape.md) — search/autocomplete endpoints return typed DTOs/records, not anonymous objects
- [`string-comparisons-explicit`](code/string-comparisons-explicit.md) — `StringComparison.Ordinal` / `OrdinalIgnoreCase`; for user search use shared `Humans.Web.Extensions` helpers
- [`time-parsing-standardization`](code/time-parsing-standardization.md) — use `TryParseInvariantTimeOnly` / `TryParseInvariantLocalTime` from `TimeParsingExtensions`
- [`view-components-vs-partials`](code/view-components-vs-partials.md) — View Component when it fetches its own data; Partial View for pure presentation. If parent fetches just to pass through, it should be a View Component
- [`viewcomponent-no-cache`](code/viewcomponent-no-cache.md) — view components must not inject `IMemoryCache`; the owning service exposes a cached accessor

## process/

Git, PRs, issues, releases, triage, build commands.

- [`about-page-license-attribution`](process/about-page-license-attribution.md) — after any NuGet update, add new versions + licenses to `Views/About/Index.cshtml`
- [`after-prod-merge-reset`](process/after-prod-merge-reset.md) — after upstream PR lands, `git fetch upstream && git reset --hard upstream/main && git push origin main --force-with-lease`
- [`discord-release-notes-format`](process/discord-release-notes-format.md) — audience-grouped (coordinators/volunteers/under-the-hood/known issues), plain-language, no emojis
- [`dotnet-verbosity-quiet`](process/dotnet-verbosity-quiet.md) — always `-v quiet` on `dotnet build`/`test`; never pipe through `tail`/`head`/`grep`
- [`ef-migration-review-gate`](process/ef-migration-review-gate.md) — MANDATORY. Run `.claude/agents/ef-migration-reviewer.md` before commit/PR; don't proceed on CRITICAL findings
- [`issue-comments-mandatory`](process/issue-comments-mandatory.md) — HARD RULE (hook-enforced). Always fetch issues/PRs with comments; Peter's comments often flip OP intent
- [`issue-no-non-peter-without-approval`](process/issue-no-non-peter-without-approval.md) — HARD RULE (hook-enforced). If `.author.login != peterdrier`, STOP and get Peter's input before code
- [`issue-refs-qualified`](process/issue-refs-qualified.md) — always `peterdrier#N` (fork) or `nobodies-collective#N` (upstream); pass `--repo` to every `gh` call
- [`maintenance-log-update`](process/maintenance-log-update.md) — after any recurring maintenance process, update `docs/architecture/maintenance-log.md` with current + next-due dates
- [`no-anon-perf-guards`](process/no-anon-perf-guards.md) — don't flag cheap `[AllowAnonymous]` DB reads as perf issues; at 500 users, an auth guard is dead defensive code
- [`no-direct-to-main`](process/no-direct-to-main.md) — HARD RULE. Always feature branch + PR, even for one-line / dev-only / "obviously safe" changes
- [`post-fix-doc-check`](process/post-fix-doc-check.md) — before final commit, scan `docs/features/` and `docs/sections/` for invariants the change touches; update inline
- [`pr-codex-thread-replies`](process/pr-codex-thread-replies.md) — reply per Codex inline thread (`POST /pulls/{n}/comments/{id}/replies`), not as top-level PR comment
- [`pr-done-means-codex-clean`](process/pr-done-means-codex-clean.md) — a PR isn't "done" until Codex returns no findings; pushed+green is mid-state
- [`pr-no-ping-reviewers`](process/pr-no-ping-reviewers.md) — don't `@codex review` after pushes; Codex quota limited, Claude reviews on push automatically
- [`pr-review-both-repos`](process/pr-review-both-repos.md) — pull comments from BOTH `peterdrier/Humans` AND `nobodies-collective/Humans`; use `/pulls/{n}/comments` for inline (not just `gh pr view`)
- [`rules-maintenance`](process/rules-maintenance.md) — when a new project rule surfaces, capture as `memory/<bucket>/<name>.md` + INDEX entry in the same commit. Don't leave it in per-machine external memory.
- [`simplify-scope-to-section-size`](process/simplify-scope-to-section-size.md) — scale `/simplify` fix counts to section LOC, not to a smaller prior PR's count
- [`todos-and-issue-tracking`](process/todos-and-issue-tracking.md) — after commits resolving items, update `todos.md` Completed section + close GitHub issues with summary + SHA
- [`triage-fetch-full-history`](process/triage-fetch-full-history.md) — `/triage` must `GET /api/feedback/{id}/messages` for every report; list endpoint counts can be stale
- [`triage-show-verbatim`](process/triage-show-verbatim.md) — `/triage` always shows reporter's verbatim Description text alongside the analysis

## product/

Terminology, restrictions, framing, deployment specifics.

- [`birthday-not-dob`](product/birthday-not-dob.md) — store birthday (month + day only); UI says "birthday", never "date of birth"
- [`coolify-build-constraint`](product/coolify-build-constraint.md) — Coolify strips `.git`; never `COPY .git` in Dockerfile; use `SOURCE_COMMIT` build arg
- [`humans-terminology`](product/humans-terminology.md) — UI uses "humans"; never "members", "volunteers", or "users". Stays in English in es/de/fr/it
- [`no-event-name-nowhere`](product/no-event-name-nowhere.md) — never use "Nowhere" in user-facing text (legal); "Elsewhere" is the current event name and is fine
- [`no-url-aliases`](product/no-url-aliases.md) — single canonical URL per page; only sanctioned alias is Barrios↔Camps (Spanish UX)
- [`profile-visibility-acceptable`](product/profile-visibility-acceptable.md) — basic profile info (name/photo/city/teams) visible to other authenticated users — including suspended/unapproved — is intentional, not a security finding
- [`vol-being-removed`](product/vol-being-removed.md) — TRANSITIONAL. `/Vol/*` is being removed; don't extend new UX or flag inconsistency with `/Shifts`
- [`voting-not-prominent`](product/voting-not-prominent.md) — Voting/Review/Applications serve ~8 people; don't headline them. Default order by daily-traffic-across-the-whole-audience
