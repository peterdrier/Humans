---
name: A removed section's migrations go with it
description: When deleting a whole section, its shipped migrations and DbContext are deleted too, once production reports its last migration applied; no stub stays.
---

Deleting a section deletes its `DbContext` and every migration file with it, as soon as production's
`/Debug/DbVersion` shows that context's last migration applied and nothing pending. Keep no stub project
for migration history.

**Why:** Peter, 2026-11-05, on the Feedback removal (peterdrier/Humans#2304): the never-delete-a-merged-migration
rule exists so no existing database breaks. Once every database has run the section's final drop, the
context owns nothing and there is nothing to break.

**How to apply:** Ship the drop migration with the section's removal, wait for production to apply it, then
delete the project in a follow-up. Before deleting, move any stored names the stability tests still require
(audit actions to AuditLog's `RetiredAuditActions`, notification sources to Notifications'
`RetiredNotificationSources`). Remove the section's `NoDestructiveMigrationOps` baseline lines with it.
Migrations of a living section stay immutable: [[no-hand-edited-migrations]].
