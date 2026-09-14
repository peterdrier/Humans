---
name: An unattended run without a compiler reads and queues, it does not edit C#
description: An unattended run with no .NET SDK does reading/doc/prose strikes only, never edits C# — record each compiler-dependent thread as skipped and queue code findings.
---

An unattended run that cannot build is not a failed run. It is a **reading** run.

**Still do:** every thread that reads — shape, behavior, freshness, conformance, prose, comments, history, inbox — and every strike confined to docs, comments and resx. Those need no compiler and are most of what a doctor run ships.

**Never do:** edit a `.cs` file. An unverified C# edit is a guess, and the run that makes it has no way to learn it was wrong — nobody is watching, and the next signal is a red CI on a PR nobody asked for.

**Always record:** each compiler-dependent thread (tests, mutation, anything gated on `dotnet build` or `dotnet test`) as **skipped, with the reason named**, in the run file's `## Skipped` block and in the PR body. A thread that silently did not run reads as a thread that found nothing.

**Queue, don't defer silently:** every code finding the reading threads turn up goes into the ranked list with its evidence, and into the run file, marked as queued-for-lack-of-a-compiler. The next run in a working environment picks it up with the analysis already done.

**Why:** the value of an unattended run is that its findings are trustworthy without supervision. Trust comes from the run knowing what it could and could not verify, and saying so — not from shipping the same volume of diff regardless of what it could check.
