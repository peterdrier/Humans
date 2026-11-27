# Audit — Language

The record of who did what to whom, kept for the Board. Settled in the 2026-11-24 terminology grill. Shared words live in the root [`CONTEXT.md`](../../../../CONTEXT.md).

## Language

**Audit**:
The section that keeps the audit log.

**Audit log**:
The whole record of what people and automation did to members and the association's data, in two sides shown together. Nothing in it is ever changed.
_Avoid_: audit trail

**Permanent side**:
The entries kept forever. Nothing on it is ever removed.

**Temporary side**:
The entries of actions marked "kept for the retention period" (one app-wide setting, a year by default). Removed once older than that, or when the person they name is erased. Shown with a "temporary" marker.

**Entry**:
One row of the audit log: one action, done once, at one moment.
_Avoid_: event (Event means the yearly event we host), record

**History**:
The part of the audit log about one thing, shown on that thing's own page ("Team history").

**Actor**:
Who did it: a person, or **System** when automation did it.

**System**:
The actor shown for automation, always with the job's name: "System (job name)".
_Avoid_: job (as the actor), nobody, unknown

**Automation**:
Work done by scheduled jobs rather than a person, in prose.

**Action**:
What was done, such as "Role assigned".

**Subject**:
The thing the action was done to.
_Avoid_: entity

**Target**:
A second thing the action involved, when there is one. In "Peter added Ana to Build Team" the team is the subject and Ana is the target.
_Avoid_: related entity

## Not the audit log

**Budget changes**:
Budget's own record of field changes, kept apart from the audit log until it moves to the temporary side (decided 2026-11-26).
_Avoid_: budget audit log
