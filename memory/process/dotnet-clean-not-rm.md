---
name: dotnet-clean-not-rm
description: For stale-metadata build errors (bin/obj corruption after a rebase or branch switch), run `dotnet clean Humans.slnx -v quiet` — never rm -rf bin/obj.
---

When the build fails with `CSC : error CS0009: Metadata file '...dll' could not be opened -- PE image doesn't contain managed metadata` after a rebase or branch switch, the fix is `dotnet clean Humans.slnx -v quiet` followed by a rebuild. Do not reach for `find ... -name obj -exec rm -rf {} +` or any rm-recursive-force variant.

**Why:** `rm -rf` is never allowed on this repo (see [[no-rm-rf]]) — a PreToolUse hook hard-blocks it. Beyond the block, `dotnet clean` is the tool built for exactly this purpose: it knows the project graph, respects MSBuild metadata, and doesn't risk collateral damage. Reaching for `rm` to delete build artifacts is a code smell even where it isn't blocked.

**How to apply:** any time a .NET build fails with stale metadata, corrupt `obj` contents, or mismatched assembly references after branch-switching or rebasing, run `dotnet clean Humans.slnx -v quiet`, then rebuild. `dotnet clean` always works; there is no fallback to a recursive delete in any shell.
