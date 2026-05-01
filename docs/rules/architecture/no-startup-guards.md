---
name: No startup guards — ever
description: HARD RULE. The Humans app must always boot. No startup checks that throw, abort, or refuse to register services. Fix at runtime, via admin button, or via idempotent migration step.
---

The application MUST always start. Never add a startup guard / pre-flight check / boot-time invariant that throws or aborts if data or environment is wrong.

**Why:** A booting app is recoverable — an admin can click a button or run a migration. A non-booting app is a production outage with no in-app recovery path. Confirmed 2026-04-29 in the email-identity-decoupling PR 2 review when an earlier draft mentioned a "startup orphan guard refuses to boot if any orphan Users remain": "we NEVER startup guard anything anytime ever in this application. The application always starts, and fixes problems if it needs to, or has admin manual fixes we can run when necessary. HARD rule."

**How to apply:**

- Don't propose startup-time invariant checks that throw / `Environment.Exit` / refuse to register services.
- For data-integrity concerns, the answer is one of:
  1. Handle gracefully at runtime (skip the bad row, log a warning, return empty result)
  2. Admin button on `/<Section>/Admin/...` that an operator clicks to repair
  3. Idempotent migration step that runs once
- Idempotent `INSERT ... WHERE NOT EXISTS` (or equivalent) inside a migration is fine — that's runtime-during-migration, not boot-time.
- If a spec or earlier-draft document mentions a startup guard, **strip it out** even if previously approved. This rule supersedes.
- Architecture test failures at build time are fine (those don't run in production); runtime boot guards are not.
