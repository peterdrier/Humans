---
name: bootstrap the .NET toolchain before any cloud run
description: Cloud containers ship no .NET SDK. Run `.claude/bootstrap-dotnet.sh` first and believe its `Full build` line — never install the SDK by hand, and never work around a denied egress host.
---

A Claude Code cloud container starts with **no `dotnet` on PATH**. A local checkout does not have this problem, so the instruction to bootstrap belongs to the jobs that run in the cloud — their own scheduled prompt — and not to the skills and docs a local session also reads. A run can tell which it is: `CLAUDE_CODE_ENTRYPOINT=remote_trigger` marks a scheduled firing, and `CCR_AGENT_PROXY_ENABLED` / `CLAUDE_CODE_CONTAINER_ID` mark a remote container generally.

**Every scheduled job's prompt that intends to build, test, measure mutation score or run reforge should open with `.claude/bootstrap-dotnet.sh`.** Skills, `CLAUDE.md` and section docs should not — a local session reading them would be told to go installing SDKs it already has.

The script installs an SDK matching `global.json`, puts `~/.dotnet/tools` on PATH, restores Stryker, installs Reforge, and prints a capability summary. Every step is skipped when already satisfied, so on a machine that has the toolchain it costs about half a second and changes nothing. The one expensive step — a real build probe — runs only after a *fallback* SDK install, where whether that SDK can compile this repo is genuinely unknown, and its verdict is cached for the life of the container.

**Why a script rather than a list of commands in each job's prompt:** the commands are not the hard part. Knowing *which* of them are even possible in a given container is, and that answer changes with the egress policy and the image. A prompt that says "run `dotnet tool restore`" fails opaquely on a container with no SDK; the script fails legibly, and every job gets the same answer from one place. It is also safe if a local session does call it — 0.5s and no changes — so a job prompt need not guard the call.

**How to apply:**

- Run it first and read the **`Full build`** line. It costs about half a second on a machine that already has the toolchain, so there is no reason to skip it or to guard the call.
- `Full build: not checked` — the normal answer on a local checkout: the SDK was already present, so the script neither installed it nor spent a minute doubting it. Proceed normally. `--probe` forces the check.
- `Full build: yes` — probed and confirmed; proceed normally.
- `Full build: unknown — probe failed` — the SDK looks fine and the probe build broke for some other reason, most likely the branch that is checked out. **Not** a docs-only session: diagnose it like any build failure.
- `Full build: NO` — this is a **docs-only session**. Work the reading threads, keep changes to docs, comments and other non-compiled files, queue every code finding for Peter rather than editing C# you cannot compile, record every compiler-dependent step as skipped-with-reason, and let the PR's CI be the compile gate. Do not edit the repo to route around the limitation.
- Never install the SDK by hand in a job prompt, and never retry or tunnel past a host the egress proxy denies with a 403 — report the blocked host instead. The script already probes the official installer once and falls back to the distro package.

**Known limitation (verified 2026-08-22; re-check before assuming it still holds):** where the official installer host is denied, the fallback is Ubuntu's `dotnet-sdk-10.0`, which is the 10.0.1xx band shipping Roslyn 5.0.0.0. `src/Humans.Analyzers` targets `Microsoft.CodeAnalysis.CSharp` 5.3.0, so every project fails `CS9057` and no build is possible. `-p:RunAnalyzers=false` does not bypass it — the analyzer assembly is loaded and version-checked before it is asked to run. Resolving it means allowing `builds.dotnet.microsoft.com`, baking a matching SDK into the image, or pinning the analyzer package down to the band the distro ships.
