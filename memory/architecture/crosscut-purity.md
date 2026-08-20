---
name: Crosscuts call no section; gather cross-lane data in an Orchestrator
description: A Crosscut (Audit, Email, Notification, Metrics) owns its own data and carries no section-specific logic — it must never call into another section. When a crosscut operation needs cross-lane data, an Orchestrator gathers it and calls the crosscut WITH the data.
---

Role vocabulary: [`CONTEXT.md`](../../CONTEXT.md) (Section / Crosscut / Orchestrator).

A **Crosscut** is a service every other section may call that carries no section-specific logic — Audit, Email, Notification, Metrics. It owns its own data (e.g. the audit log) but **reaches into no other section.** A Crosscut calling a Section is wrong-direction: everything calls the Crosscut, so a back-call risks a loop and couples the tool to a section's schema.

**When a crosscut operation needs data from other lanes, invert it:** an **Orchestrator** gathers the data and calls the Crosscut *with* it. The Crosscut never reaches out.

Canonical case — "audit for this user, following merged accounts" needs the merged-account id set, which lives in the User/merge data. Audit must NOT call `IUserServiceRead.GetMergedSourceIdsAsync` for it. Instead an **Audit orchestrator** gathers the merged ids and calls Audit with the list. (`AuditLogService` currently violates this and is tagged `[DontFix]` pending that orchestrator; `RoleAssignmentService` is the Auth sibling case.)

**Fan-out contributions are the second sanctioned mechanism** (Peter, 2026-08-19, nobodies-collective/Humans#1059): for uniform per-section resolution — the case in point is turning a bare Guid into `(type, display name)` for AuditLog, Search, and anything else holding Guids — the crosscut owns a contributor interface that sections implement and register. That keeps the direction clean: sections reference the contract (inbound implementations), the consumer still calls no section. Prefer fan-out over per-consumer orchestrator plumbing when every section answers the same question. Landed as `IEntityNameContributor` in `Humans.Base.Interfaces` — Base, not one consumer's contracts leaf, because two consumers must not reference each other.

Terminology note (same ruling): the horizontal/vertical tier language retires. The binding form of this rule is **wide-fan-in sections (Auth, Audit, Users) never gain outbound links to other sections** — loops there break the whole system; keep inter-project edges minimal, especially outbound from widely-used assemblies. `peters-hard-rules.md` still carries the old wording; that edit is Peter's to make.

Same direction as the foundational outbound-zero rule ([[user-profile-foundational]]) and the consumer-resolves rule (design-rules §6b): orchestrators and consumers reach *down*; tools and foundations never reach *up*. Marker/ownership side of this is [[orchestrator-marker]].
