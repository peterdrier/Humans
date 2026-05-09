<!-- freshness:triggers
  src/Humans.Application/Services/Teams/**
  src/Humans.Web/Controllers/TeamController.cs
  src/Humans.Web/Controllers/TeamAdminController.cs
  src/Humans.Web/Controllers/HumansTeamControllerBase.cs
  src/Humans.Web/Views/Team/**
  src/Humans.Web/Views/TeamAdmin/**
-->
<!-- freshness:flag-on-change
  User-facing flows: browse/join/leave, public team pages, role assignment UX. Section invariants and data model live in docs/sections/Teams.md.
-->

# Teams & Working Groups

> **Section invariants** (data model, entity fields, system-team sync rules, routing, authorization, owning services): [`docs/sections/Teams.md`](../../sections/Teams.md). This file is the user-facing spec — stories, acceptance criteria, and workflows.

## Business Context

Nobodies Collective operates through self-organizing working groups (teams). Teams can be created for specific initiatives and managed by their members. Teams can optionally be organized into departments (parent-child hierarchy) for logical grouping. Three system-managed teams automatically track key organizational roles: all volunteers, all team coordinators, and board members.

## User Stories

### US-6.1: Browse Available Teams
**As a** member
**I want to** see all active teams in the organization
**So that** I can discover groups I might want to join

**Acceptance Criteria:**
- Page split into two sections: "My Teams" at top, "Other Teams" below
- "My Teams" shows teams the user belongs to (empty state: "You haven't joined any teams yet")
- "Other Teams" shows remaining teams with pagination
- Each team card shows name, description, member count, role badge, and system badge
- Shows if team requires approval to join
- Distinguishes system teams from user-created teams
- Separate `/Teams/My` page retained for Leave/Manage actions

### US-6.2: View Team Details
**As a** member
**I want to** view detailed information about a team
**So that** I can decide if I want to join

**Acceptance Criteria:**
- Shows team name, description, creation date
- Lists all current members with roles
- Shows coordinator(s) who manage the team
- Displays join requirements (open vs approval)
- Shows my current relationship with the team

### US-6.3: Join Team (Open)
**As a** member
**I want to** join a team that doesn't require approval
**So that** I can immediately participate

**Acceptance Criteria:**
- One-click join for open teams
- Immediately added as Member role
- Redirected to team page
- Google resources access granted

### US-6.4: Request to Join Team
**As a** member
**I want to** request to join a team that requires approval
**So that** the coordinators can review my request

**Acceptance Criteria:**
- Can submit request with optional message
- Request enters Pending status
- Cannot submit if already have pending request
- Can withdraw pending request

### US-6.5: Approve/Reject Join Requests
**As a** team coordinator or board member
**I want to** review and process join requests
**So that** appropriate members can join the team

**Acceptance Criteria:**
- View list of pending requests for my teams
- See requester info and their message
- Approve (adds member) or reject (with reason)
- Notification sent to requester

### US-6.6: Leave Team
**As a** team member
**I want to** leave a team I'm no longer participating in
**So that** I'm not listed as an active member

**Acceptance Criteria:**
- Can leave any user-created team
- Cannot leave system teams (auto-managed)
- Membership soft-deleted (LeftAt set)
- Google resources access revoked

### US-6.7: Manage Team Members
**As a** team coordinator or board member
**I want to** manage team membership and roles
**So that** the team is properly organized

**Acceptance Criteria:**
- View all team members
- Assign coordinator role via management role definition
- Remove member from team
- Cannot modify system team membership

### US-6.8: Create Team (Admin)
**As a** board member
**I want to** create new teams for organizational initiatives
**So that** members can organize around specific projects

**Acceptance Criteria:**
- Specify team name and description
- Choose if approval is required
- Optionally assign a parent team (department)
- System generates URL-friendly slug
- Team is immediately active

### US-6.9: View Public Team Page (Anonymous)
**As an** anonymous visitor
**I want to** view a team's public page
**So that** I can learn about the team before joining

**Acceptance Criteria:**
- Anonymous visitors can access `/Teams/{slug}` for public teams
- Shows team description, page content (markdown), and call-to-action buttons
- Shows coordinators with display name and avatar (no email or contact info)
- Regular members are hidden from anonymous visitors
- Non-public teams return 404 for anonymous visitors
- System teams and sub-teams cannot be made public

### US-6.10: Edit Team Page Content (Coordinator/Admin)
**As a** team coordinator, board member, or admin
**I want to** edit my team's public page content
**So that** I can provide information to potential volunteers

**Acceptance Criteria:**
- Edit page at `/Teams/{slug}/EditPage`
- Toggle public visibility (only for departments, not sub-teams or system teams)
- Write page content in markdown format
- Configure up to 3 call-to-action buttons (text + URL + style)
- Only one CTA can be styled as Primary
- Changes are audit-logged with `TeamPageContentUpdated`
- Edit Page link appears in team management sidebar

### US-6.11: Browse Public Team Directory (Anonymous)
**As an** anonymous visitor
**I want to** browse the public team directory
**So that** I can discover teams I might want to join

**Acceptance Criteria:**
- Anonymous visitors can access `/Teams` and see only public teams
- Shows team name, description snippet, and "Learn More" link
- No My Teams section, no admin buttons, no system teams shown
- Authenticated users see the full existing layout unchanged
- Subteams only appear if `IsPromotedToDirectory` is true; top-level teams always appear

## Workflows

### Join Request State Machine

```
                  +---------+
                  | Pending |
                  +----+----+
                       |
        +--------------+--------------+
        |              |              |
   +----v----+   +----v----+   +----v----+
   | Approve |   | Reject  |   |Withdraw |
   +----+----+   +----+----+   +----+----+
        |              |              |
   +----v----+   +----v----+   +----v-----+
   |Approved |   |Rejected |   |Withdrawn |
   |         |   |         |   |          |
   |(+Member)|   |         |   |          |
   +---------+   +---------+   +----------+
```

### Direct Join (No Approval)

```
User clicks "Join"
        |
        v
+-------------------+
| Create TeamMember |
| Role = Member     |
| JoinedAt = now    |
+---------+---------+
          |
          v
+-------------------+
| Sync Google       |
| Resources         |
+---------+---------+
          |
          v
    [User is member]
```

### Approval Join

```
User submits request
        |
        v
+-------------------+
| Create            |
| TeamJoinRequest   |
| Status = Pending  |
+---------+---------+
          |
    [Wait for review]
          |
          v
+---------------------+
| Coordinator/Board   |
| reviews request     |
+---------+-----------+
          |
    +-----+-----+
    |           |
 Approve     Reject
    |           |
    v           v
+--------+  +--------+
|+Member |  |Notify  |
|+Google |  |User    |
+--------+  +--------+
```

### Leave

```
User clicks "Leave"
        |
        v
+-------------------+
| Validate:         |
| - Not system team |
| - Is member       |
+---------+---------+
          |
          v
+-------------------+
| Set LeftAt = now  |
| (soft delete)     |
+---------+---------+
          |
          v
+-------------------+
| Revoke Google     |
| resource access   |
+---------+---------+
          |
          v
    [User removed]
```

## Related Features

- [Authentication](../auth/authentication.md) — Board role enables team creation
- [Volunteer Status](../onboarding/volunteer-status.md) — Determines Volunteers team membership
- [Google Integration](../google-integration/google-integration.md) — Team resource provisioning
- [Background Jobs](../global/background-jobs.md) — System team sync job
- [Hidden Teams](hidden-teams.md) — Admin-only team visibility flag
