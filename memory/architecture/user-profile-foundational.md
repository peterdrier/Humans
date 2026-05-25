---
name: User and Profile are foundational — no outbound calls to higher-level sections
description: UserService and ProfileService sit at the bottom of the dependency stack. They must not call out to Teams, Shifts, Tickets, Campaigns, Applications, Google, Legal, Governance. Crosscuts (Audit, Email, Notification, Metrics) are the only OK exceptions.
---

> Vocabulary ([`CONTEXT.md`](../../CONTEXT.md)): **foundational** is a *descriptor* (a Section with outbound-width-into-sections = 0), not a separate tier; the "universal crosscuts" below are **Crosscuts** (Audit/Email/Notification/Metrics).

User and Profile sit at the bottom of the service dependency hierarchy. Higher-level sections (Teams, Shifts, Tickets, Campaigns, Applications, Google, Legal, Governance) can freely call into User/Profile. **The reverse is wrong direction and must be avoided.**

The only outbound calls User/Profile may make are to universal crosscuts: `IAuditLogService`, `IEmailService`, `INotificationService`, `IHumansMetrics`. Even those should be minimised.

**Why:** Peter stated this explicitly during the 2026-04-23 post-§15-Part-1 review. The dependency graph showed `ProfileService` had 14 outbound ctor injections and `UserService` had 4 eager + 4 lazy — most pointing at higher-level sections. The resulting Profile↔User, User↔Role, User↔Shifts lazy cycles are all symptoms of this wrong-direction coupling. Making the foundational layer pure collapses a whole class of architectural complexity.

**How to apply:**

Before adding a dependency on `UserService` or `ProfileService` from a higher-level section, that's fine. Before having User/Profile call OUT to a higher-level section, **stop.** Use one of these inversion patterns:

- **Read-for-display** (profile page wants tickets/campaigns/applications data) → move the assembly up to the caller (controller/view-model). Don't let Profile aggregate.
- **Data that really belongs on Profile** (membership level, event-attendance flag) → denormalize onto Profile; the owning section pushes updates via an inbound call (Applications → Profile on approval, Tickets → Profile on event-ticket purchase).
- **Cascade-on-delete** → extract an orchestrator (e.g. `IAccountDeletionService`) that lives above User/Profile and calls each section to clean up.
- **Cache invalidation across sections** → narrow invalidator interface owned by the foundational service and implemented by the higher-level section (same pattern as `IShiftAuthorizationInvalidator`, `INavBadgeCacheInvalidator`, `IFullProfileInvalidator`).

If a dependency seems unavoidable, the feature is probably in the wrong service — check whether it belongs on a higher-level orchestrator instead.

Interface narrowing (e.g., Profile injecting a narrow query interface instead of a full service) is acceptable but doesn't fix the direction — only a loose-coupling mitigation. Prefer true inversion: relocate the predicate/write to the leaf that owns the field. See `memory/architecture/no-leaf-to-director-callbacks.md`.
