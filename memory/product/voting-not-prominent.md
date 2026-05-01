---
name: Don't make Voting/Review/Applications prominent
description: Board voting, review queues, and tier applications serve ~8 people. Don't put them at the top of dashboards, sidebars, stat tiles, or feature ordering — design for the 800 humans, not the 8 board members.
---

Stop privileging Voting / Review / Applications in design proposals. Their audience is the Board (~8 people); the rest of the system serves ~800 humans. Putting these queues in the first stat tile, the first sidebar group, the dashboard centerpiece, or the lead user-story is a structural error — it makes the most niche workflow look like the spine of the product.

**Why:** Direct correction from Peter on the admin-shell brainstorm — "8 people care about the voting, not the 800 other people in the app." A `/Admin` dashboard built around pending approvals + open votes + recent activity got flagged as a recurring pattern: "stop doing that." The Review/Voting/Application surfaces look high-stakes structurally — state machines, audit trails, governance language — but they're rarely-trafficked. Mistaking structural complexity for product centrality is the bug.

**How to apply:**

- Default order in dashboards / sidebars / nav / stat rows is by **daily-traffic-across-the-whole-audience**, not by structural prominence.
- High-traffic admin surfaces (defaults to top): Volunteers / shifts / staffing, Tickets / scanner, Humans (member directory), Camps, Teams, Feedback triage.
- Lower priority, but still on screen: Voting, Review, Board materials, tier applications.
- Don't lead a stat row with "pending approvals" or "open votes." Lead with operational scale (active humans, shift coverage %, open feedback) and put approvals/votes in the sidebar pills where the 8 people who care will find them.
- Same rule for grouping: an "Onboarding & members" group that contains Review should not be the first sidebar group. "Operations" (Volunteers, Tickets, Scanner) goes first.
- Applies equally to feature spec drafting, user-story ordering, and release-notes sequencing — don't write up Voting features as if they're the headline.
