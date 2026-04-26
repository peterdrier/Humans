# Teams

## What this section is for

Teams are how humans organize around a shared purpose. A team is either a **department** (top-level, like Build or Kitchen) or a **sub-team** that lives under exactly one department. Each team can have coordinators, named role slots, and optionally a `@nobodies.team` Google Group plus a linked Shared Drive folder.

A few teams are **system teams** (Volunteers, Coordinators, Board, Asociados, Colaboradores) — the app manages those automatically, so you cannot join or leave them by hand. Some teams are also **hidden**: privacy-sensitive groupings visible only to admins.

## Key pages at a glance

- **Teams directory** (`/Teams`) — landing page; your teams, other departments, system teams. Anonymous visitors see only teams with a public page.
- **Team detail** (`/Teams/{slug}`) — description, members, coordinators, sub-teams, join/leave.
- **My teams** (`/Teams/My`) — your teams with Leave / Manage buttons.
- **Birthdays** (`/Teams/Birthdays`) — teammate birthday calendar (month + day).
- **Roster** (`/Teams/Roster`) — cross-team view of named role slots.
- **Members admin** (`/Teams/{slug}/Members`) — members, join requests, role assignments.
- **Edit team page** (`/Teams/{slug}/EditPage`) — markdown and calls-to-action for a department's public page.
- **Roles** (`/Teams/{slug}/Roles`) — define named role slots.
- **Summary**, **Create**, **Edit**, **Sync** — admin pages at `/Teams/Summary`, `/Teams/Create`, `/Teams/{id}/Edit`, `/Teams/Sync`.

## As a Volunteer

### See the teams you're in

Go to `/Teams/My`. Each card shows your role (Member or Coordinator) and a Leave button for user-created teams. System teams appear here too but have no Leave button.

### Browse and discover teams

Open `/Teams`. "My Teams" sits at the top, "Other Teams" below with pagination. Each card shows name, short description, member count, and whether it requires approval. Click through to the team detail page for the full description, coordinators, and (for departments) the public page content.

![TODO: screenshot — Teams directory showing "My Teams" at top and "Other Teams" cards below with member counts and approval badges]

### Join a team

On the team detail page, click **Join**. Open teams add you immediately and grant Google Group / Drive access on the next sync. Teams that require approval create a pending join request (with an optional message); you can withdraw it any time. You cannot have two pending requests to the same team.

### Leave a team

From `/Teams/My` or the team detail page, click **Leave**. Your membership is soft-removed and Google access is revoked on the next sync. You cannot leave system teams.

### See teammates

On a team detail page you can see other active humans on the team. Basic profile info follows the normal [Profiles](Profiles.md) rules.

### Your team's resources

Joining a team automatically gives you access to the team's `@nobodies.team` group email and (where set up) its Shared Drive folder. Find the Drive folder via Teams → your team → Resources. Don't share Drive links directly with people who aren't on the team — add them through Humans so access is tracked, and so it goes away cleanly when their role ends. Email setup is covered in [Email](Email.md); the sync mechanics are in [GoogleIntegration](GoogleIntegration.md).

### When and where you arrive on site (2026)

Your coordinator gives you a specific arrival date — not a range, not "come when you can". A date. Site is near Castejón de Monegros, Huesca, Spain; the nearest train station is Sariñena (~30 minutes away). An ORG bus service runs on key arrival days; details land closer to Elsewhere.

| Date | Who arrives |
|---|---|
| ~12 June | Placement crew only |
| 15 June | First crew: Barrio Support, Cantina, Power, Production & Logistics |
| 22 June | Set-up week — most departments start arriving |
| 29 June | Pre-event week — barrios and artists arrive |
| 7 July | Gates open |
| 13–22 July | Strike. Stay as long as you can. |

Don't arrive earlier than your confirmed date without checking — early arrivals consume kitchen and infrastructure capacity before they're ready.

### On-site contacts (2026)

| For | Who |
|---|---|
| Role questions | Your team coordinator |
| Shift questions or changes | Frank, Nurse, or Hardcastle (vol-coord), or your coordinator |
| Production, logistics, vendors | Daniela — [daniela@nobodies.team](mailto:daniela@nobodies.team) |
| Welfare and wellbeing | Malfare / Participant Wellness |
| Information, lost and found | No Info (on-site information point) |
| Emergency | Radio — channel given on arrival |

## As a Coordinator

(assumes Volunteer knowledge)

A **Coordinator** is a human assigned to a department's management role, with full authority over the department and every sub-team under it. **Sub-team managers** have the same tools scoped to a single sub-team — no access to the parent department, sibling sub-teams, or Google resources. See [Glossary](Glossary.md#coordinator).

### Manage members and join requests

Open `/Teams/{slug}/Members`. Approve or reject pending join requests (with optional review notes), add existing humans directly, or remove members. Removing a member also removes their role assignments on that team; all changes are audit-logged.

### Edit the team's public page

For departments, go to `/Teams/{slug}/EditPage`. Write the body in markdown, toggle public visibility, and configure up to three call-to-action buttons (text + URL + Primary or Secondary style; only one may be Primary). Sub-teams and system teams cannot have public pages.

### Manage role slots

At `/Teams/{slug}/Roles` you define named roles (e.g., "Social Media Lead"), set slot counts and priority, and mark one role per team as the management role. Assigning a human to a role auto-adds them to the team.

### Manage Google Group and Drive (departments only)

Department coordinators manage the linked Google Group membership and Shared Drive folder permissions. Sub-team managers cannot — Google resources live at the department level and roll up automatically. See [Email](Email.md) for the human-facing side and [GoogleIntegration](GoogleIntegration.md) for sync mechanics.

> **Don't share Drive links directly with people who aren't on your team in Humans.** It creates ungoverned access and a GDPR problem. Add them through Humans and Drive access follows automatically; remove them and it's revoked overnight.

### Volunteer coordination, cross-department (2026)

Three roles handle volunteer coordination across every department. Reach all three at [volunteers@nobodies.team](mailto:volunteers@nobodies.team) or in [#🤝-recruitment-relationships](https://discord.gg/pcq2DRH6) on Discord.

| Role | Person | What they cover |
|---|---|---|
| Pre-production | Frank | Year-round: recruitment, leads, pre-production |
| On-site coordination | Nurse | Pre-event/on-site volunteer coordination during set-up and event |
| On-site management | Hardcastle | Set-up and on-site management; the shift system |

### Sub-team leads who need shift management permissions

In many departments it's the sub-team leads — not the top-level coordinator — who manage day-to-day shifts. Example: in Creativity, shifts are managed by Kunsthaus and MoN leads, not the Creativity coordinator. The system is being updated to reflect this properly. In the meantime, if a sub-team lead needs shift management permissions, ask Frank to escalate their access to TeamAdmin.

## As a Board member / Admin (Teams Admin)

(assumes Coordinator knowledge)

### Create a team

`/Teams/Create` (TeamsAdmin, Board, Admin). Set name, description, approval mode, optional parent department, optional Google Group prefix, and the hidden flag. The slug is auto-generated and can be overridden on edit.

### Edit team settings

`/Teams/{id}/Edit` lets you change name, slug, approval mode, parent, Google Group prefix, directory promotion (for sub-teams), and `IsHidden`. Making a department into a sub-team re-scopes its coordinators to managers and syncs them out of the Coordinators system team.

### Delete a team

**Board** and **Admin** can delete (deactivate) user-created teams. System teams cannot be deleted.

### Hidden teams

Toggle `IsHidden` on create or edit. Hidden teams do not appear in the directory, profile cards, birthday lists, or "My Teams" for non-admins; campaigns can still target them by ID.

### System team sync

Admins view sync status at `/Teams/Sync` and (Admin only) run immediate syncs. The hourly `SystemTeamSyncJob` keeps Volunteers, Coordinators, and Board membership aligned with role assignments and consent status.

## Related sections

- [Profiles](Profiles.md) — team membership feeds profile cards and visibility.
- [Shifts](Shifts.md) — rotas are owned by a department or sub-team; coordinator/manager status drives shift management.
- [Camps](Camps.md) — camps are team-owned; team membership feeds camp planning.
- [GoogleIntegration](GoogleIntegration.md) — how team membership maps to Google Groups and Shared Drive folders.
- [Governance](Governance.md) — Board and tier roles feed the Board and Colaborador/Asociado system teams.
- [Glossary](Glossary.md#coordinator) — coordinator, manager, department, sub-team, system team definitions.
