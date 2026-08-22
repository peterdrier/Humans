---
name: bootstrap the .NET toolchain before any cloud run
description: Cloud containers ship no .NET SDK. Run `.claude/bootstrap-dotnet.sh` first and believe its `Full build` line — never install the SDK by hand, and never work around a denied egress host.
---

A Claude Code cloud container starts with **no `dotnet` on PATH**. Any scheduled job or remote session that intends to build, test, measure mutation score, or run reforge must run `.claude/bootstrap-dotnet.sh` before its first phase.

The script installs an SDK matching `global.json`, puts `~/.dotnet/tools` on PATH, restores Stryker, installs Reforge, then probes a real build and prints a capability summary. It is idempotent — running it on an already-configured machine is a no-op that just reports state.

**Why a script rather than a list of commands in each job's prompt:** the commands are not the hard part. Knowing *which* of them are even possible in a given container is, and that answer changes with the egress policy and the image. A prompt that says "run `dotnet tool restore`" fails opaquely on a container with no SDK; the script fails legibly, and every job gets the same answer.

**How to apply:**

- Run it first, read the summary, and treat the **`Full build`** line as authoritative for the rest of the session.
- `Full build: yes` — proceed normally; build and test before every commit as usual.
- `Full build: NO` — this is a **docs-only session**. Work the reading threads, keep changes to docs, comments and other non-compiled files, queue every code finding for Peter rather than editing C# you cannot compile, record every compiler-dependent step as skipped-with-reason, and let the PR's CI be the compile gate. Do not edit the repo to route around the limitation.
- Never install the SDK by hand in a job prompt, and never retry or tunnel past a host the egress proxy denies with a 403 — report the blocked host instead. The script already probes the official installer once and falls back to the distro package.

**Known limitation (verified 2026-08-22; re-check before assuming it still holds):** where the official installer host is denied, the fallback is Ubuntu's `dotnet-sdk-10.0`, which is the 10.0.1xx band shipping Roslyn 5.0.0.0. `src/Humans.Analyzers` targets `Microsoft.CodeAnalysis.CSharp` 5.3.0, so every project fails `CS9057` and no build is possible. `-p:RunAnalyzers=false` does not bypass it — the analyzer assembly is loaded and version-checked before it is asked to run. Resolving it means allowing `builds.dotnet.microsoft.com`, baking a matching SDK into the image, or pinning the analyzer package down to the band the distro ships.
