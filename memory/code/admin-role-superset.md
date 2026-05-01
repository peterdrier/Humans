---
name: Admin and domain-admin roles are supersets
description: Admin can do everything system-wide. Domain-specific *Admin roles (TeamsAdmin, CampAdmin, TicketAdmin) are supersets within their domain. Always include both in role lists.
---

**Admin** can do everything in the entire system that any other role can do. Hard rule, no known exceptions.

Each domain-specific `*Admin` role is a superset of all capabilities within its domain:
- **TeamsAdmin** — everything under `/Teams`
- **CampAdmin** — everything under `/Camps`
- **TicketAdmin** — everything under `/Tickets`

**Why:** These are administrative roles scoped to their domain. They should never be locked out of functionality within their own area.

**How to apply:**

When adding `[Authorize(Roles = "...")]` or role checks, always include `Admin`. Within a domain area, also include the relevant `*Admin` role. When reviewing existing authorization, flag any place where Admin or a domain admin is excluded from capabilities in their area. Use `RoleGroups` constants (e.g. `RoleGroups.BoardOrAdmin`) instead of hand-maintained role-name lists.
