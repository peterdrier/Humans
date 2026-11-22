---
name: user-profile-foundational
description: UserService/ProfileService sit at the bottom of the stack — no outbound calls to Teams, Shifts, Tickets, etc. Only Crosscuts (Audit, Email, Notification, Metrics) are exempt.
---

Users (user and profile data) sits at the bottom of the service dependency hierarchy. Every other
section may call into it. It never calls out to a higher-level section; the only outbound calls allowed
are to Crosscuts (`IAuditLogService`, `IEmailService`, `INotificationService`, `IHumansMetrics`), and
those sparingly.

**Why:** outbound injections from the user and profile services into higher sections produced a family
of lazy dependency cycles. A pure foundation removes that whole class of complexity.

**How to apply — when Users seems to need another section, invert:**
- **Read for display** (a profile page wants tickets or applications) → assemble in the caller
  (controller, view model, a section-contributed part). Users does not aggregate.
- **Data that truly belongs on the profile** → store it there; the owning section pushes updates in.
- **Cascade on delete** → an orchestrator above Users (`IAccountDeletionService`) calls each section.
- **Cross-section cache invalidation** → don't. The cache owner evicts on its own writes; no section
  flushes another's cache (HUM0028).

A dependency that seems unavoidable usually means the feature belongs on an orchestrator. Injecting a
narrowed interface doesn't fix the direction —
[`no-leaf-to-director-callbacks`](no-leaf-to-director-callbacks.md).
