---
name: Never hardcode fabricated content into views
description: Don't invent user-facing copy (benefits, policies, pricing, deadlines, vendor lists, "tips", FAQs) and ship it hardcoded. If there's no authoritative source, wire to an admin-editable field or ask.
---

Never fabricate user-facing content and ship it hardcoded in a view, partial, seed, or localization string. This includes benefits lists, obligations, policies, pricing, deadlines, resource links, vendor lists, "tips", FAQs, event rules, or any domain copy that sounds like it should come from an admin or a document.

**Why:** PR 216 invented a full "What You Need to Know Before Registering Your Barrio" accordion with made-up benefits (power access tiers, water supply, placement rules), obligations (shifts, Leave No Trace), and resources (containers, pricing, vendors) — none grounded in any source. The actual design was an admin-editable markdown textarea (`CityPlanningSettings.RegistrationInfo`) rendered at the top of the register page. The hardcoded accordion had to be ripped out.

**How to apply:**

If a task asks for "info about X on page Y" and you don't have a concrete source (issue body, linked doc, existing copy elsewhere in the repo), do not invent it. Either:
1. Wire the page to an admin-editable field and leave the content empty
2. Ask the user for the source text
3. Ask whether the content should be hardcoded or admin-managed

A blank card waiting for real copy is always better than a convincingly-worded fabrication. Domain content (especially policy, pricing, or commitments to members) is a red flag — if you're tempted to write "will be shared before the season begins" or "tier-based access" without a source, **stop**.
