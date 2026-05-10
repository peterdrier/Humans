---
name: Model tiering — Opus orchestrates, Sonnet executes, Haiku snips
description: Use the Agent tool's `model` param to dispatch mechanical work to cheaper models. Opus stays on judgment calls (design dialogue, architectural reasoning, what-counts-as-a-rule); Sonnet handles self-contained refactors; Haiku does surgical one-shots. Saves ~5-10x on mechanical token volume.
---

The `Agent` tool accepts a `model` param: `"sonnet"`, `"opus"`, or `"haiku"`. The orchestrator (the Claude reading this) is usually Opus. It can dispatch subagents on cheaper models for the mechanical bulk of a session.

**Why:** A long refactor session (e.g. #685 ProfileService decomposition, 2026-05-09) splits into ~30% judgment work and ~70% mechanical execution. Running the whole thing on Opus costs 5x what the same outcome costs with judicious Sonnet dispatch. Peter flagged the under-use during that session's debrief.

**How to apply:**

### What stays on Opus

- Design dialogue with Peter (95%-confidence loop, multi-question batches, judgment calls)
- The "is this a domain concept or a UI artifact?" kind of architectural reasoning
- Move-map authoring — the part where you decide what moves where
- Memory-atom writing (deciding what's a durable rule vs ephemeral context is judgment)
- Resolution comments on issues
- Final verification: did the subagent's diff actually match the spec?

### What goes to Sonnet (`Agent` with `model: "sonnet"`)

Self-contained mechanical refactors with a crisp brief:

- "Apply this move map: drop methods X/Y/Z from IProfileService, drop these 4 ctor deps, delete these wrappers from CachingProfileService. Build cleanly. Report back."
- "ProfileController gains these 4 injections. Refactor the Me/Edit-GET/Edit-POST/AdminDetail actions to compose Profile + Application data inline. Build cleanly."
- "Fix all test compilation errors after the IUserService.X signature change. Update fakes, drop tests for moved methods, build + run cleanly."
- Build-fail → fix → re-build loops where each error is a known-pattern fix.
- Boilerplate propagation across many files (signature changes, ctor updates, decorator wrappers).

### What goes to Haiku (`Agent` with `model: "haiku"`)

**Two distinct uses — both high-leverage on Opus orchestrator context.**

**(a) Surgical one-shot edits** in a single file with explicit before/after:

- "In `tests/.../Foo.baseline.txt`, append these 2 lines in alphabetical order."
- "In file X line range Y-Z, change signature from A to B."
- Format-only fixes.

**(b) Context-vacuum readers — the high-value use.** Haiku reads the bulky thing and returns a tiny summary. The bulk never touches Opus's context.

Highest-leverage targets:

- **Build logs.** `dotnet build > /tmp/build.log 2>&1; echo "exit=$?"`. If non-zero, dispatch Haiku: "List each compilation error as `<path>:<line> CS#### <message>`. No preamble, no fix suggestions. If no errors, output `NO_ERRORS`." Returns 5 lines instead of 50.
- **Test logs.** "For each failed test: `[name] file.cs:line — reason`. Skip stack traces, skip passes."
- **Diff inspection** for large changes: "Summarize each file's changes at the conceptual level, one bullet per file."
- **File shape discovery.** "Read X. List public method signatures + line numbers. Skip bodies." Saves Opus from pulling 800 lines just to refresh the mental model.
- **Long log file scans** (`/api/logs`, hangfire dumps): "Find errors matching <pattern>. Return as `timestamp message`. Drop everything else."

Rules for vacuum prompts:

- **Be brutally explicit about format.** "No preamble, no summary, no fix suggestions, no commentary." Otherwise Haiku adds fluff that defeats the point.
- **Verify before acting destructively.** If the next step is an Edit based on Haiku's summary, spot-check one finding (re-Read the underlying file at that line). Paraphrase risk on Haiku is non-zero.
- **Don't dispatch for small things.** Subagent spin-up is ~10-20s and ~$0.001 minimum. For a <500-line Read, just do it inline. Vacuum pays off on build logs, test logs, large files, long log scans.

Haiku struggles with multi-file context and architectural reasoning. Use for parsing, not deciding. For "is this an arch smell?" → Sonnet/Opus. For "list the errors in this file" → Haiku.

### Constraints

- **Hard limit of 3 parallel subagents** (Peter's standing rule, per CLAUDE.md). Sequential dependencies serial; independent files can fan out 3-wide.
- **Subagents can't do interactive design.** Don't dispatch a Sonnet to "decide whether to use approach A or B." Decide first, then dispatch the chosen approach.
- **Subagent summaries describe intent, not always shipped reality.** Verify via `git diff --stat` + spot-check before trusting "done."
- **Subagent context is separate.** Brief it with everything it needs (file paths, exact methods, the move map). It can't see your chat with Peter.
- **Cost amortization.** Don't dispatch a 30-second task that takes a 60-second subagent spin-up. Use Sonnet when the task is genuinely batch-shaped — multiple files or a build-fix loop, not one Edit.

### When to dispatch (timing within a session)

The right moment is *immediately after the design dialogue closes*. The move map is fresh, the brief is crisp, the orchestrator hasn't yet built up sunk-cost momentum on doing the work itself. If you wait until you're 5 build-fail-loops deep, the dispatch threshold feels too high to bother — that's the trap.
