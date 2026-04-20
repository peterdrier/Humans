# 05_coordinator_vs_member — sonnet

**Category:** roles  
**Duration:** 4462 ms  
**Cost:** $0.0114  
**Tokens:** input=100, cache_read=27065, cache_write=0, output=202

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

As Kitchen coordinator, you have these capabilities in Shifts that regular volunteers don't:

| Feature | You | Regular Volunteer |
|---|---|---|
| **Create/edit rotas & shifts** | ✅ (Kitchen + its sub-teams) | ❌ |
| **Approve/refuse signups** | ✅ (Kitchen + its sub-teams) | ❌ |
| **Voluntell** humans | ✅ (Kitchen + its sub-teams) | ❌ |
| **Bail other humans' signups** | ✅ | ❌ |

Like all volunteers, you can also browse shifts, sign up, view your own schedule, and set availability.

**Scope note:** Your coordinator powers are limited to the Kitchen department and any sub-teams within it — you can't manage rotas or signups for other departments.
