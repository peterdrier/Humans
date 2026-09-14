---
name: After editing any .cs file, re-Read it to surface LSP diagnostics
description: After editing a `.cs` file, re-Read it — the `csharp-ls` LSP fires diagnostics on Read, not Edit.
---

The `csharp-ls` LSP is active via the `csharp-lsp` Claude Code plugin. It provides real-time C# compiler diagnostics (type errors, missing usings, nullable warnings, etc.) on `.cs` files when they are read.

**After editing any `.cs` file, re-read it before moving on.** Diagnostics appear on `Read`, not on `Edit`. This catches errors immediately without waiting for a full `dotnet build`. Always fix LSP-reported errors in the current file before editing the next one.
