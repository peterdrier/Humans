---
name: No build-time version stamping
description: Never stamp generated build inputs (version, git tag, timestamp) from state a design-time build lacks; `dotnet format` and IDE loads then force a full recompile.
---

Do not add build-time stamping of generated compile inputs (`AssemblyInfo.cs`, version files) from git tags, commit height, or the clock. The only build identity Humans carries is the commit hash, set by the `SourceRevisionId` target in `Directory.Build.props` for `Humans.Web` alone.

**Why:** MinVer (removed 2026-09) skipped design-time builds, so every `dotnet format Humans.slnx` run rewrote all ~70 projects' generated `AssemblyInfo.cs` with the SDK default `1.0.0` where the real build had written `0.0.0-alpha.0.N`. The next `dotnet build` or `dotnet test` saw changed inputs everywhere and recompiled the whole solution: 100 s per format-then-build cycle, on every machine and every agent session, and hot reload failed with `CS7038`. Nothing consumed the version: QA and production both reported `0.0.0-alpha.0`.

**How to apply:** A design-time build (IDE load, `dotnet format`, analyzers) and a real build must produce byte-identical generated inputs. Before adding anything under `obj/` that depends on git or time, run `dotnet build`, then `dotnet format ... --verify-no-changes`, then `dotnet build` again: the second build must emit no compiler output. If a real version number is ever needed, pass it in at publish time (`-p:Version=`), never derive it inside the build.
