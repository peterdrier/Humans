---
name: /triage must fetch full message history before drafting responses
description: For every feedback report being triaged, GET /api/feedback/{id}/messages — list endpoint counts can be stale and Peter may have replied manually.
---

When running `/triage` (or any feedback-handling work), ALWAYS fetch the full message thread via `GET /api/feedback/{id}/messages` for **every** report being triaged — not just the ones the list endpoint flags as having messages.

**Why:** The feedback list endpoint's `messageCount` / `lastAdminMessageAt` fields can be stale or lag behind recent activity. Peter may have responded to a report manually between the list fetch and the triage session. Drafting a fresh response without checking leads to:
- Sending duplicate messages to the reporter (looks unprofessional)
- Re-asking for info Peter already asked for
- Missing that Peter already resolved the issue conversationally
- Wasted effort drafting responses that won't be sent

**How to apply:** In Phase 2 of `/triage`, right after fetching the feedback list, loop through ALL open/acknowledged reports and fetch their messages. Include the existing thread in the analysis presented to Peter — note for each report whether admin has already replied, when, and with what. Only draft new responses for reports with no prior admin message or where the reporter has replied since the last admin message. Applies to close-phase notifications too (don't blindly send a "shipped!" message if someone already responded to that thread).
