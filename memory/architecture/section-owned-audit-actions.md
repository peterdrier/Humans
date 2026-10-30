---
name: Section-owned audit actions
description: A section that writes an audit row declares the action itself (an `AuditActionDefinition` static field on its `<Section>AuditActions` contributor); never add anything to Humans.AuditLog for it. The `AuditAction` enum and `LegacyAuditActions` are transitional.
---

An audit action belongs to the section that writes it. Declare it as a `public static readonly AuditActionDefinition` field on the section's `<Section>AuditActions : AuditActionContributor` class, register that class with `services.AddSingleton<IAuditActionContributor, <Section>AuditActions>()` in `Section.Register`, and pass the field to `IAuditLogService.LogAsync` and the two filtered reads.

**Why:** the central `AuditAction` enum, the verb switch, the quick-filter buttons and the anomaly count all made a new section edit Humans.AuditLog (the PR #2118 symptom). A definition carries its own stored name, English verb and self-verb, description-tail flag, alert flag and quick-filter label; `AuditActionRegistry` collects the contributors at startup, fails on a duplicate or over-long name, and resolves stored names back to definitions for rendering.

**How to apply:**

- The `Name` is the stored column value: never rename one once rows exist. A name nobody declares any more still renders, as a bare definition with no verb.
- During the move, `LegacyAuditActions` (in Humans.AuditLog) declares every enum value and `IAuditLogService` keeps `AuditAction` overloads that store the enum's name. A section that moves its actions deletes them from `LegacyAuditActions` in the same PR and switches its call sites and tests to its own fields. Both go with the enum once the last section has moved.
- Translated badge labels and verbs stay in the hub resx, keyed by name (`Enum_AuditAction_{Name}`, `AuditLog_{Verb|SelfVerb|NoSubjectVerb}_{Name}`); a definition without entries renders its English verb and its name split into words.
- Document the section's actions in its `Docs/<Section>.md`; `AuditLog.md` lists none once the catalog block goes.
