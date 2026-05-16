---
name: No suppressions of Humans architecture analyzers
description: HARD RULE — never use #pragma warning disable, [SuppressMessage], or // ReSharper disable to silence HUM* analyzers or other architecture findings. Always fix the underlying structural mistake.
---

There is no path under which suppressing a Humans architecture analyzer (HUM* diagnostic id) is the right answer. Always fix the underlying architectural mistake.

`#pragma warning disable HUM<NNNN>`, `[SuppressMessage("HUM<NNNN>", ...)]`, and `// ReSharper disable [once] <RuleId>` for HUM rules are forbidden as a way to silence a custom architecture analyzer finding. The same prohibition applies to ReSharper findings that flag real architectural drift.

**Why:** The analyzers exist to enforce design rules. Silencing one without fixing the underlying violation defeats the analyzer's purpose. Memory atoms that "explain" a suppression encode the avoidance rather than the fix and are equally forbidden.

**How to apply:** When an analyzer fires, the answer is to fix the architecture, not to disable the analyzer. Concrete examples of "fix the architecture":

- The `[Section(...)]` attribute on a type is mistagged — retag it.
- The interface's `[SurfaceBudget]` is too low for the legitimate surface area — raise the budget (the rule against raising is about papering over creep, not refusing to size a budget correctly for a real refactor).
- A service is in the wrong namespace/folder for its section — move it (e.g. `memory/architecture/team-resources-google-integration-section.md`).
- An interface does not expose a method it structurally should — add it (within budget).
- A service has a DI cycle because two services mutually depend — break the cycle by extracting the shared logic to a third type, or by inverting the dependency.

If after exhausting structural fixes the architecture is genuinely incoherent at that point, that is a real finding — report it back with the specific architectural incoherence named, and **the PR does not ship** until the architecture is fixed. The suppression is never the answer.

**Scope:** Applies to all `HUM*` analyzer ids (e.g. HUM0001 through the current HUM* set, plus the named HUM_* ids like HUM_USER_DISPLAYNAME, HUM_PROFILE_ISSUSPENDED, etc.) and to ReSharper findings that flag architectural drift. Does not apply to language-level warnings (e.g. CS0414) that genuinely cannot be addressed structurally — those follow normal C# practice.
