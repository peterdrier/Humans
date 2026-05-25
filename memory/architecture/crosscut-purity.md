---
name: Crosscuts call no section; gather cross-lane data in an Orchestrator
description: A Crosscut (Audit, Email, Notification, Metrics) owns its own data and carries no section-specific logic — it must never call into another section. When a crosscut operation needs cross-lane data, an Orchestrator gathers it and calls the crosscut WITH the data.
---

Role vocabulary: [`CONTEXT.md`](../../CONTEXT.md) (Section / Crosscut / Orchestrator).

A **Crosscut** is a service every other section may call that carries no section-specific logic — Audit, Email, Notification, Metrics. It owns its own data (e.g. the audit log) but **reaches into no other section.** A Crosscut calling a Section is wrong-direction: everything calls the Crosscut, so a back-call risks a loop and couples the tool to a section's schema.

**When a crosscut operation needs data from other lanes, invert it:** an **Orchestrator** gathers the data and calls the Crosscut *with* it. The Crosscut never reaches out.

Canonical case — "audit for this user, following merged accounts" needs the merged-account id set, which lives in the User/merge data. Audit must NOT call `IUserServiceRead.GetMergedSourceIdsAsync` for it. Instead an **Audit orchestrator** gathers the merged ids and calls Audit with the list. (`AuditLogService` currently violates this and is tagged `[DontFix]` pending that orchestrator; `RoleAssignmentService` is the Auth sibling case.)

Same direction as the foundational outbound-zero rule ([[user-profile-foundational]]) and the consumer-resolves rule (design-rules §6b): orchestrators and consumers reach *down*; tools and foundations never reach *up*. Marker/ownership side of this is [[orchestrator-marker]].
