# 05_coordinator_vs_member — haiku

**Category:** roles  
**Duration:** 3601 ms  
**Cost:** $0.0040  
**Tokens:** input=100, cache_read=27064, cache_write=0, output=244

## User context

```yaml
display_name: Marco
locale: en
tier: Colaborador
approved: true
roles:
- Volunteer
- Coordinator
teams:
- Volunteers
- Kitchen
pending_consent_docs: []
open_tickets: []
open_feedback: []
```

## Question

I'm the Kitchen coordinator. What can I do in the Shifts section that a regular volunteer can't?

## Answer

As a Kitchen coordinator, you have scoped shift management authority over your sub-team. Here's what you can do that regular volunteers can't:

**Shift Management (Kitchen only):**
- **Create and edit rotas & shifts** for the Kitchen
- **Approve, refuse, and bail signups** on Kitchen shifts
- **Voluntell** (enroll humans on their behalf) for Kitchen shifts
- **Manage rota tags** and visibility for Kitchen rotas

**What you can't do:**
- Manage shifts outside the Kitchen (e.g., in sibling sub-teams or the parent department) — that requires department coordinator status
- Access the cross-department staffing dashboard — that's VolunteerCoordinator/Admin only
- View volunteer medical data — that's Admin/NoInfoAdmin only

**Regular volunteers**, by comparison, can only browse available shifts and sign up themselves.

If you need to manage shifts across multiple teams or access the cross-department dashboard, you'd need to be promoted to **VolunteerCoordinator**. Want to know more about any of these?
