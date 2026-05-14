---
name: reforge
description: Roslyn-powered semantic query CLI for C# solutions. Use when you need to find references, callers, implementations, dependencies, members, or trace call chains in a C# codebase — replaces multi-round grep/read cycles with single precise queries.
---

Run `reforge skill` to get the full usage guide:

```bash
reforge skill
```

Follow the output as your reference for which command to use and how to interpret results.

For large solutions, start a hot server first to avoid the cold start tax:

```bash
reforge serve --solution path/to/Solution.slnx &
```

Then all subsequent `reforge` commands auto-relay to the server (~200ms instead of 3-20s).

If the `reforge` binary is not on the PATH, ask the user for permission to install it via `dotnet tool install --global Reforge` (source: https://github.com/peterdrier/reforge) before proceeding.
