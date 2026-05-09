---
name: Context discipline — read narrow, build to file, batch edits
description: Concrete habits that keep the context window from filling up during multi-file refactors. The patterns below collectively saved ~60% of session tokens in retrospect; each is independently adoptable.
---

When refactoring across many files, the context window fills with the same kinds of repeat offenders. These are the levers that actually move the needle.

**Why:** The 2026-05-09 #685 ProfileService decomposition session burned context on Reads that were 80% irrelevant code, build-output preambles that repeated every iteration, and 3-4 sequential Edits to the same file when one Write would have done. Peter flagged it directly. The patterns below address each cause.

**How to apply:**

### 1. Read narrow ranges, not 100-line chunks

- Grep first to get the exact line, then `Read` with a tight `offset` + `limit` window (10-30 lines max). When the diff target is one method, 5-15 lines is plenty.
- Don't re-Read after a successful Edit "to confirm." The harness errors on stale state — silence is success.
- Don't re-Read when chained `Edit`s on the same file fail; the file path is already in your context. Use the `system-reminder` shown by Edit failures to re-anchor.
- For deletions, you often don't need to Read the body at all — grep the signature, then Edit with the signature as `old_string` (you can match large blocks if `old_string` is unique).

### 2. Pipe build output to a file, query incrementally

Don't pipe `2>&1 | tail -50` — that drops the preamble but still pulls 50 lines into context every iteration, and most of those lines are project-restored noise.

Instead:

```bash
dotnet build Humans.slnx -v quiet > /tmp/build.log 2>&1; \
  grep -c "error" /tmp/build.log
grep -E "(error|warning)" /tmp/build.log | head -5
```

Then `Read /tmp/build.log` with `offset` to surgically pull each error's surrounding context only when needed. Same pattern for test runs:

```bash
dotnet test Humans.slnx > /tmp/test.log 2>&1
grep -A 3 "FAIL" /tmp/test.log | head -30
```

Counts and one-line summaries are nearly free; let the file hold the bulk.

### 3. One Write beats five Edits when the file changes a lot

If the file needs more than ~3 surgical edits, do one `Read` (full file) then one `Write` (full new file). The total tokens are about the same, but the round-trip count drops, the user sees one cleaner diff, and you don't accumulate the per-Edit `system-reminder` tax.

Threshold heuristic: 3 edits or fewer → use `Edit`. 4+ edits or whole-method-replacement → use `Write` (after a single `Read` to satisfy the must-Read-first rule).

### 4. Use /reforge for "find callers / find implementations / cross-reference symbols"

`/reforge` is a Roslyn-powered semantic CLI. It replaces multi-step grep cycles like "where is X called?" → grep → "what does the call site look like?" → Read → "what's the signature there?" → grep again. Reforge gives you all three in one query.

Use reforge whenever you would otherwise:
- Grep for a method name to find call sites (use `reforge references <symbol>`)
- Grep for an interface to find implementations (`reforge implementations <interface>`)
- Trace a method's outbound calls (`reforge callees <method>`)
- Audit an interface surface before refactoring (`reforge audit-surface` or the `audit-surface` skill)

Plain grep is still right for: text searches that aren't symbols (string literals, comments), `using` statements, file-name globs, and one-off lookups you're already 80% sure about. But for anything that touches the symbol graph, reforge wins.

### 5. Commit checkpoints during long refactors

If a multi-file change is held entirely in working-tree limbo until the end, every file's contents stay context-relevant for the whole session. Commit logical chunks as you go (interfaces, then implementations, then tests, then docs) — once committed, the diff is in git and you don't need to keep re-reading the same files to remember what you changed.

### 6. cd-once-then-absolute-paths

`cd` doesn't persist between separate `Bash` calls (the docs claim it does, but in practice it doesn't). Don't waste round-trips testing this. Use absolute paths everywhere, or `git -C <path>` for git, or pass the full path to `dotnet`. Stop trying to maintain shell state.
