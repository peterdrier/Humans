---
name: Debug section — developer/diagnostics home at /Debug
description: Developer/diagnostics pages live in the Debug section at /Debug/* (DebugController, AdminOnly) — forward home for logs, db/cache stats, client stats. Not /Admin/*.
metadata:
  type: project
---

**Developer and diagnostics pages live in the `Debug` section at `/Debug/*`** (`DebugController`, class-level `[Authorize(Policy = AdminOnly)]`). This is the forward home for logs, DB/cache stats, client/request stats, and any "tool a developer wants" — replacing the legacy, frozen `/Admin/*` diagnostics tree.

**Why:** Per [[no-admin-url-section]] there is no `/Admin/` section going forward. Diagnostics are cross-cutting — owned by no domain section — so they get their own `Debug` section. Peter (2026-05-22): *"Gut says Debug.. DebugController, /Debug/foo. that honestly holds logs/diagnostics/anything a developer wants. db/cache stats, etc."*

**How to apply:**

- New developer/diagnostics pages: `/Debug/<Page>` on `DebugController`, all `AdminOnly`.
- Pages sit at `/Debug/*` directly, **not** `/Debug/Admin/*`: the whole section is admin-gated with no user-facing pages, so there is no public-vs-admin split. (The `/<Section>/Admin/*` shape in [[no-admin-url-section]] disambiguates admin from public actions inside a *mixed* section; Debug is admin-only end to end.)
- Do **not** migrate the legacy `/Admin/*` diagnostics (Logs, DbStats, CacheStats, Configuration, Maintenance) as part of unrelated work — that's a separate refactor Peter will scope; they land in `/Debug/*` when he does.
- Section contract: [`docs/sections/Debug.md`](../../docs/sections/Debug.md).
