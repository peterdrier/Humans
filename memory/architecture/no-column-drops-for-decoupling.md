---
name: no-column-drops-for-decoupling
description: HARD RULE. When decoupling code from a column, a property override plus read sweeps completes the work — the column drop is separate, optional, and never part of the decoupling PR.
---

When the goal is "stop relying on a column as the source of truth", **the property override plus read
sweeps are the migration.** The column drop is separate and optional.

**Why:** Peter: *"we can stop using columns of data and not drop them. you're overly complicating
things again … Stop doing dangerous things."* An override that reads from the new source achieves the
goal with no schema change and is reversible by deleting it. The attempt to drop/`Ignore()` the email
column made every existing user disappear from the preview UI.

**A decoupling PR is complete when:**
1. The property override (or equivalent shim) reads from the new source of truth.
2. Read sweeps route new and active code paths through the canonical service.
3. Writes to the old column have stopped.
4. Tests pass.

**It does not need to:** drop the column, `Ignore()` it in the EF mapping, hide it behind a custom
store, or refactor every legacy query that still reads it. Those queries keep working because the
column is still there.

**How to apply:**
- A spec line like "PR 2 drops the column" is revised to "PR 2 makes the column unused".
- Unsure? Leave the column, ship the override, move on. It can stay indefinitely.
- A drop that is wanted later is its own PR under
  [`destructive-drops-need-approval`](destructive-drops-need-approval.md).
