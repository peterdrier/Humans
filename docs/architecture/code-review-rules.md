<!-- freshness:flag-on-change
  Reviewer-facing reject rules (Razor booleans, authorization gaps, missing .Include, exception swallowing, cache invalidation, JSON serialization, migration integrity). Flag if any architectural pattern shift in src/** changes what reviewers must catch.
-->

# Code Review Rules

Hard rules for code review. Every item here is a **reject** — if any of these are violated, the review must flag it as CRITICAL regardless of context. These rules are passed verbatim to all reviewers (Claude, Codex, Gemini) via `/code-review`.

Rules are ordered by historical frequency — the patterns that have caused the most bugs in this codebase appear first.

## PR Scope

- **Review the full PR, not just the diff.** A PR review must include the PR description, existing review comments, inline comments, prior findings, and release context. Do not post an approval or release verdict until those have been read and reconciled.
- **Do not contradict unresolved credible findings without addressing them explicitly.** If an earlier review comment raises a real issue, either agree with it or explain precisely why it is not valid before approving the PR.

## Razor Boolean Attributes *(8+ historical fixes)*

- **Never use `disabled="@boolValue"` or `readonly="@boolValue"`.** HTML boolean attributes are active when *present*, regardless of value. `disabled="False"` still disables the element. Use Razor conditionals: `@if (condition) { <input disabled /> }` or the tag helper `asp-for` with conditional attributes.
- **Same applies to `checked`, `selected`, `multiple`, `required`, `hidden`, `open`.** Any HTML boolean attribute rendered with a C# expression will be present in the DOM even when the value is `false`.

## Authorization Gaps *(8+ historical fixes)*

- **Every controller action that modifies data must have `[Authorize]` with explicit roles.** Missing `[Authorize]` on a POST/PUT/DELETE action is a security hole. Don't rely on controller-level `[Authorize]` alone if individual actions need different role requirements.
- **Every POST action must have `[ValidateAntiForgeryToken]`.** No exceptions.
- **Admin actions must check admin roles, not just `[Authorize]`.** Bare `[Authorize]` only checks authentication, not authorization. Actions in Admin/TeamAdmin/ShiftAdmin/CampAdmin controllers need role-based authorization.
- **Manual role checks in method bodies should be attributes.** Replace `if (!User.IsInRole("Admin"))` patterns with `[Authorize(Roles = "Admin")]` on the action.

## Missing .Include() *(6+ historical fixes)*

- **Every LINQ query that accesses a navigation property must `.Include()` it.** EF Core does not lazy-load. Accessing `entity.RelatedEntity.Property` without a prior `.Include(e => e.RelatedEntity)` returns null — no exception, just silent null data that causes downstream bugs.
- **Check `.ThenInclude()` for nested navigation.** If you access `entity.Parent.Children`, you need `.Include(e => e.Parent).ThenInclude(p => p.Children)`.
- **Projection (`Select`) does not need `.Include()`** — only materialize queries that access navigation properties on tracked entities.

## Silent Exception Swallowing *(5+ historical fixes)*

- **Every `catch` block must log the exception.** Use `_logger.LogError(ex, "Failed to {action} for {context}")` with structured logging. A catch block with no logging, no re-throw, and no explanatory comment is a reject.
- **`catch (Exception)` without the variable is always a reject.** If you're catching, you must log — and you can't log without the exception variable.
- **External service calls (Google API, email, HTTP) must be wrapped in try-catch with logging.** Unhandled exceptions from external services crash the request.

## Orphaned Pages *(5+ historical fixes)*

- **Every controller action must be reachable from UI.** No dead endpoints — if an action exists, there must be a form, link, or JS call that invokes it. Unreachable actions are dead code that misleads and rots.
- **Every new page must have a nav link** (per CLAUDE.md). No orphan pages.

## Cache Invalidation *(5+ historical fixes)*

- **Every mutation (create/update/delete) must evict affected cache entries.** If a service caches a list or entity and another method modifies the underlying data, the cache key must be evicted in the same method. Stale cache = users see old data.
- **Cache eviction goes in the same service method as the mutation**, not in the controller or a separate call. The service owns the cache, so it owns the invalidation.
- **When adding a new cache key, grep for all mutations of the cached data** and add eviction to each one.

## Form Field Preservation *(4+ historical fixes)*

- **When validation fails and the form is re-displayed, all user input must be preserved.** This means repopulating ViewBag/ViewData items (dropdowns, lists) and returning the model with the submitted values. If the user fills 10 fields and one fails validation, they must not lose the other 9.
- **Dropdown/select lists must be repopulated before returning the view.** ViewBag items set in the GET action are not automatically available in the POST action — you must set them again before `return View(model)`.

## JSON Serialization *(4+ historical fixes)*

- **Never rename `[JsonPropertyName]` attributes or properties on serialized DTOs.** Renaming breaks deserialization of stored data. This includes any class with `[JsonPolymorphic]`, `[JsonDerivedType]`, `[JsonConstructor]`, or `[JsonInclude]` attributes.
- **New properties on serialized types must have `[JsonInclude]` if they have no public setter.** System.Text.Json skips properties without public setters unless explicitly included.

## Migration Integrity *(4+ historical fixes)*

- **No hand-edited migrations.** Migration files must be exactly as `dotnet ef migrations add` generated them. Any `migrationBuilder.Sql()` call, manual column tweak, or inserted comment that wasn't scaffolded is a reject. Data cleanup goes in separate scripts or application code, never in migrations.
- **No deleted or renamed migration files** that have already been committed. Generate a new migration for corrections.

## EF Core Bool Sentinel *(3+ historical fixes)*

- **Never use `HasDefaultValue(false)` on boolean properties.** EF Core treats `false` as the sentinel value for bools. If you configure `HasDefaultValue(false)`, EF Core thinks an explicitly-set `false` is "unset" and sends the default instead. Set the value directly on the entity instance, not via `HasDefaultValue`.
- **Same trap applies to `HasDefaultValue(0)` on integers and `HasDefaultValue("")` on strings.** The default of the CLR type is always the sentinel.

## Service Method Parity

- **Batch/range methods must enforce the same guards as their single-item counterparts.** If `SignUpAsync` checks AdminOnly, duplicates, overlap, capacity, EE cap, and system-open — then `SignUpRangeAsync` must check all of those too. Missing a guard in a batch path is an authorization or data integrity bypass. Same principle for `BailRangeAsync` vs `BailAsync`, or any future bulk operation.
- **Batch operations on stateful entities must filter by valid status.** If a domain method throws on invalid state transitions (e.g., `Bail()` throws on already-Bailed signups), the caller must filter to only valid-status records before calling. Never load all records and hope they're in the right state.

## View / Controller Consistency

- **Data-creating buttons must have idempotency guards.** If clicking "Generate Shifts" twice would double-create, the button must be hidden or disabled after the first use. Applies to any bulk creation UI.
- **Success messages must reflect actual results**, not independently computed values. If the service creates N items, the message should use N from the result, not re-derive it from input parameters (which can diverge, e.g., negative counts).

## Type Safety in Views

- **No lossy casts in display logic.** `(int)duration.TotalHours` silently drops fractional hours. Use `PlusMinutes((int)duration.TotalMinutes)` or equivalent. Applies to any numeric conversion where precision matters for display.
- **JS array indices for model binding must be contiguous.** ASP.NET MVC model binding breaks on gaps (e.g., `[0], [2]` after removing `[1]`). When removing dynamic form rows, reindex remaining elements.

## Content Security Policy

- **No inline event handlers.** Never use `onclick`, `onsubmit`, `onchange` etc. in HTML attributes — CSP blocks them. Use `data-*` attributes and attach event listeners in a nonce'd `<script>` block. Pattern: `data-confirm="message"` + `.addEventListener('click', ...)` in the page's script section.

## Dead Code

- **No unused variables, unreachable code, or orphan imports** in committed code. If a reviewer spots `var x = ...; // never read`, it's a reject.

## Test Attribute Policy

- **Bare `[Fact]` and `[Theory]` are forbidden in test code.** All test methods must use `[HumansFact]` / `[HumansTheory]` from `Humans.Testing` (5s default timeout, project-wide). Enforced via `BannedApiAnalyzers` rule `RS0030`.
- **No `RS0030` suppressions in test code.** Pragma escape hatches (`#pragma warning disable RS0030`) are forbidden anywhere in `tests/` outside `tests/Humans.Testing/` (where the replacement attributes are declared). CI (`Forbid RS0030 suppressions in test code` step) fails the build if any are found.
- **`Timeout = 0` (or negative) on `HumansFact` / `HumansTheory` is forbidden.** The setter throws `ArgumentException` at attribute construction. To allow a longer cap on a slow test, set `Timeout = N` with `N > 0` (typical: `10000` for DB-fixture-heavy tests, `30000` for tests with explicit retry/backoff timing). Infinite timeout is not an option.
