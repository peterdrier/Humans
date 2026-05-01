---
name: Profile visibility to other users is intentional, not a security finding
description: Basic profile info (name, photo, city, teams) visible to other authenticated users — including suspended/unapproved users — is intentional. Don't flag in security review or gate it on IsSuspended/IsApproved.
---

A user's basic profile info (display name, photo, city, teams) being visible to other authenticated users in the system is **not a privacy concern**. This includes suspended users and unapproved users.

**Why:** This is a membership system for a small nonprofit (~500 users). Members are expected to see each other — that's the point. Suspended users are typically in temporary states (missing consent re-signs, etc.) or under admin review; hiding their basic identity from other members creates confusing UX without meaningful privacy benefit. Suspension reasons, admin notes, and internal flags are the sensitive bits — not names and faces.

**How to apply:** When reviewing auth/visibility code, don't flag "suspended user is visible via popover/search" as a security issue. Don't suggest gating the popover, profile card, team roster, or search on `IsSuspended` or `IsApproved`. The controls that matter:

- Access to the app itself (suspended users lose `ActiveMember` claim)
- Admin-only data (notes, suspension reasons, consent status)
- The ability to *act* as a volunteer (shift signups, team joins, etc.)

Pre-#213, the Popover endpoint returned 404 for suspended users. That was removed intentionally as part of "cache all profiles, filter at consumer level." Don't try to restore it.
