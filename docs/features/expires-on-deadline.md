<!-- freshness:triggers
  src/Humans.Domain/Architecture/ExpiresOnAttribute.cs
  src/Humans.Analyzers/ExpiresOnAnalyzer.cs
  Directory.Build.props
-->
<!-- freshness:flag-on-change
  Severity escalation contract, graceDays semantics, or the WarningsNotAsErrors entries for HUM0010/HUM0011 — review when these change.
-->

# `[ExpiresOn]` — Hard removal deadlines for deprecated symbols

`[ExpiresOn("yyyy-MM-dd", graceDays: 7, reason: "...")]` decorates any symbol with a removal deadline. The build clock drives diagnostics from warning to error on that date, so deprecations cannot accumulate indefinitely the way `[Obsolete]` warnings do.

## Business Context

`[Obsolete]` is open-ended. A deprecation tagged in 2024 produces the same warning in 2026, and the warning blends into background noise. New code occasionally adds new callers because the warning is the same one that has been there for two years. Eventually the deprecated thing has more callers than when it was first marked, defeating the deprecation.

`[ExpiresOn]` ships a clock. The deadline lives in source, in the same place a maintainer would look. The build progressively breaks on schedule whether or not anyone is paying attention. Two weeks out for the typical case — long enough for a normal migration, short enough that the deadline survives in working memory.

## Escalation Contract

Each `[ExpiresOn]` attribute carries one date and one `graceDays` window (default 7).

**HUM0010 — usage sites.** Every reference to the decorated symbol (call, property/field/event/method-group access, or `new`) emits a diagnostic.

| Build date relative to `date`     | Severity   |
| --------------------------------- | ---------- |
| Before `date`                     | Warning    |
| On or after `date`                | Error      |

**HUM0011 — declaration site.** The decorated symbol itself emits a diagnostic only after the date passes.

| Build date relative to `date`              | Severity   |
| ------------------------------------------ | ---------- |
| Before `date`                              | (clean)    |
| `date` ≤ today < `date + graceDays`        | Warning    |
| `date + graceDays` ≤ today                 | Error      |

The grace period is asymmetric on purpose: callers feel the deadline a week earlier than the author of the symbol. By the time the declaration starts erroring, callers have already been broken for `graceDays`, so the symbol is unreachable and safe to delete.

"Today" is `DateTime.UtcNow.Date` on the build machine. A clean CI build on the deadline day flips red without any code change — which is exactly the point of a deadline.

## Why warnings must stay warnings

The repo runs with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. Without an exemption, HUM0010 and HUM0011 would be hard build breaks the moment the attribute is added, defeating the staged escalation.

`Directory.Build.props` exempts both rule IDs via `<WarningsNotAsErrors>`. Past-deadline diagnostics are still emitted with `effectiveSeverity: Error` directly from the analyzer (which bypasses `WarningsNotAsErrors`), so the cliff is preserved.

## `graceDays` Semantics

`graceDays` extends only HUM0011 (declaration). HUM0010 (usage) flips to error on `date` regardless of `graceDays`. The default of 7 is suitable for most cases; raise it when the migration is large enough that callers might need extra time to be cleaned up after the deadline (the declaration stays compilable as a warning longer).

A `graceDays` of 0 means the declaration errors on the same day callers do.

## Malformed dates are silent no-ops

`TryReadAttribute` requires `yyyy-MM-dd` ISO format. If the date string fails to parse, the analyzer skips that attribute and continues walking — it does not error, does not warn, does not block the build. The reasoning: a malformed deadline string is a configuration bug at the attribute site, not at the call site, and breaking unrelated callers for a typo isn't useful. When a member-level attribute is malformed but its containing type carries a valid `[ExpiresOn]`, the containing-type attribute still fires.

## Authoring

```csharp
[ExpiresOn("2026-05-26", reason: "Replaced by IUserEmailService.UpdateEmailAsync")]
public Task LegacyUpdateEmail(string email) { ... }
```

Combine with `[Obsolete]` when both apply — `[Obsolete]` carries the migration story and the alternative API; `[ExpiresOn]` carries the deadline. They layer cleanly.

`[ExpiresOn]` lives in `Humans.Domain.Architecture` so any layer can use it; the attribute has no runtime behavior.

## Currently Set Deadlines

| Symbol                  | Date       | Grace | Tracking issue |
| ----------------------- | ---------- | ----- | -------------- |
| `User.NormalizedEmail`  | 2026-05-18 | 7     | #635           |
