---
name: Falling through to a switch default logs a warning
description: Use SwitchDefaultWarn (Humans.Base.Extensions.LoggerSwitchExtensions) when a switch/lookup falls through to default because the value is genuinely unknown — new enum member, new status. Skip it when the default is the deliberately-correct branch.
---

A `default:` label or `_ =>` arm that means "a value I did not know about" logs a warning naming the enum type, the value, and the call site — not silently. Silently taking the fallback turns a new enum member into wrong-but-plausible output (nobodies-collective/Humans#1065; `EnumBadgeMap.For` rendered new statuses grey with nothing to catch it once the per-section architecture tests covering it were deleted in peterdrier/Humans#1327).

**Why:** A default that silently substitutes a plausible-looking value (a badge color, an empty set, a fallback string) hides the bug instead of surfacing it — the code keeps running, looks fine, and is wrong. A logged warning turns that into a line in the log viewer the first time it happens, for every enum, with no test to remember to write.

**How to apply:**

```csharp
logger.SwitchDefaultWarn(value); // ILogger extension, Humans.Base.Extensions
```

Call it from the `default:`/`_ =>` branch before returning the fallback. `site` defaults to `[CallerMemberName]`; pass it explicitly only when the call isn't made from the method whose name should appear in the log.

A DI-less static class (no constructor to receive an `ILogger`) can't use the extension — see `EnumBadgeMap.For` for the pattern: a private static `Serilog.ILogger` field defaulting to `Log.ForContext<T>()`, with an internal test-only setter.

**Skip it when the default is deliberately correct**, not a stand-in for "unknown":
- The default already throws or otherwise surfaces loudly (an exception is louder than a log line).
- The default is merged with a named case (`case X: default:`) as a documented, deliberate choice (e.g. `NotificationInboxFilter.All` and `default` both mean "no filter").
- The switch is a type-pattern dispatch guard where most values are expected to miss (an `AuthorizationHandler` matching only the resource shapes it cares about).
- The fallback is a genuinely open-ended range, not an enum (`count switch { <= 0 => ..., _ => ... }`).

**Related:** [`base-ui-registries-are-section-populated`](../architecture/base-ui-registries-are-section-populated.md) — `EnumBadgeMap`'s no-DI-logger problem and why Base can't just inject one.
