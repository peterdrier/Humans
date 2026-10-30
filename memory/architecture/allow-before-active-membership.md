---
name: allow-before-active-membership
description: When a page or action must work for a signed-in account that is not yet Active (onboarding, the status wall, own-profile maintenance), mark it `[AllowBeforeActiveMembership]` in its own section — the Shell's membership gate keeps no list.
---

A controller or action a non-Active account must still reach carries `[AllowBeforeActiveMembership]`
(`Humans.Base.Attributes`). `MembershipRequiredFilter` (Shell) reads the action's attribute first, then the
controller's; everything else, apart from `[AllowAnonymous]`, waits for `UserState.Active`. `Terminal = true`
extends the allowance to Deleted and Merged accounts and belongs only on the status wall.

**Why:** the gate used to hold a by-name list of other sections' controllers and actions, so adding a page a
new member needs meant editing the Shell (peterdrier/Humans#2118 did). The attribute keeps the decision where
the page lives, next to its `[Authorize]`.

**How to apply:**
- New page a Bare, DeletePending or walled account must reach → the attribute on the action (or the whole
  controller when every action qualifies), and a line in the section's `Docs/authorization.md`.
- Public pages and sign-in use `[AllowAnonymous]`, not this.
- `MembershipRequiredFilterTests` (Web.Tests) covers the gate; the section's own tests cover the page.

Related: [[crosscut-purity]].
