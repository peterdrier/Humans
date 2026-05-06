# 40. Agent Section

## Business Context

Humans have questions about Nobodies operations that today are absorbed by coordinators or the issues widget. The Assistant answers grounded questions ("why is my consent check pending?", "what's the difference between Colaborador and Asociado?") using our own docs and the user's live state. It does NOT take actions on the user's behalf — it explains, cites, and proposes an issue draft via `route_to_issue` when it can't answer; the user reviews and submits.

## User Stories

### US-40.1 — Ask a grounded question
As a **signed-in human** I want to **type a question about how the system works** so that **I get an answer grounded on our documentation and my current state, in my preferred language**.

**Acceptance:**
- The Assistant panel is reachable from the floating "Help" launcher whenever `AgentSettings.Enabled = true`.
- An "AI Terms" link below the composer opens `/Legal/agent-chat`. There is no explicit consent gate — opening the panel and sending a message constitutes use; the linked Terms describe what's sent, retention, admin visibility, and rights.
- Response streams token-by-token within 2s of submission (SSE).
- Per-turn user context attached to the prompt includes: profile basics, governance roles, **team memberships in `TeamName: RoleName` per-team format** (so the agent can answer "what teams am I on?" without a tool call), **open ticket IDs**, and an **upcoming-shifts tail** (confirmed/pending signups in the active event with `EndDate >= today`, multi-day range signups conflated to one entry per `SignupBlockId`).
- The agent has a `get_shift_details` tool that takes a `Key` from the upcoming-shifts tail and returns rota/date-range/hours/practical-info for that signup. Lookups are scoped to the calling user's own signups (privacy guard) and filtered to upcoming-only days, matching the `UpcomingShifts` row the user selected.
- When the docs don't cover the question, the agent calls `route_to_issue` with a proposed `{title, category, description}`. The server emits an `AgentIssueProposal` SSE frame; the client opens the Issues submission modal pre-filled. The user reviews and submits via `/Issues/Submit` — no row is written server-side from the agent.

### US-40.2 — See my past conversations
As a **signed-in human** I want to **review my previous agent conversations** so that **I can find an earlier answer or pick up a thread**.

**Acceptance:**
- `GET /Agent/Conversations` lists the user's conversations with `StartedAt`, `LastMessageAt`, `MessageCount`.
- `GET /Agent/Conversations/{id}` shows the transcript. No user-initiated delete endpoint — retention purges old conversations on the daily schedule.

### US-40.3 — Admin reviews agent behavior
As an **Admin** I want to **see all agent conversations and refusals** so that **I can spot-check quality and feed corrections back into docs**.

**Acceptance:**
- `/Agent/Conversations` adapts to the role: regular users see only their own; Admins see all conversations with a Human column and filters for refusals-only, handoffs-only, and per-user.
- `/Agent/Conversations/{id}` shows additional Admin-only details (token counts, tool invocations, "Show what would be sent to Anthropic" link).
- `/Agent/Admin/Settings` exposes `Enabled`, `Model`, caps, `PreloadConfig` (Tier1/Tier2).

### US-40.4 — Admin disables under abuse
As an **Admin** I want to **disable the agent globally with one setting change** so that **I can react to abuse or provider outages immediately**.

**Acceptance:**
- Setting `Enabled = false` hides the Assistant menu item and returns 503 from `/Agent/Ask` within the next request (store refreshes on write).

## Data Model

Reference: `src/Humans.Domain/Entities/Agent*.cs`. Key entities: `AgentConversation`, `AgentMessage`, `AgentSettings`. Per-user rate-limit counters are in-memory (Singleton `IAgentRateLimitStore`), no DB table.

Legacy: `FeedbackReport.Source` (`AgentUnresolved`) and `FeedbackReport.AgentConversationId` exist for historical rows from the previous server-side handoff. The Agent no longer writes them.

## Workflows

### Turn workflow
`User submits` → `enabled gate` → `rate-limit check` → `abuse check` → `prompt assembly` (includes the per-turn user-context snapshot — profile, governance roles, team memberships in `TeamName: RoleName` form, open ticket IDs, upcoming-shifts tail) → `Anthropic streaming call (with cached prefix)` → `[tool loop, max 3]` → `persist messages` → `stream finalizer`.

### `get_shift_details` lookup
Agent calls `get_shift_details({shiftId: <Key>})` with the `Key` from a row in the upcoming-shifts tail → `IAgentToolDispatcher` resolves against the user's own signups (privacy guard), filtered to upcoming-only days via the same `GetAbsoluteEnd > now` filter that built the tail → returns rota name + date range + day count + status + description + hours + practical info as a text block. Returns "Shift not found." on any miss (unknown id, other user's signup, fully-past block).

### Handoff workflow (propose-only)
Tool call `route_to_issue` with `{title, category, description}` → dispatcher returns a proposal-marker without DB writes → `AgentService.ParseIssueProposalArgs` decodes the args → `AgentService` yields an `AgentIssueProposal` SSE frame → client opens the Issues modal pre-filled → user submits via `/Issues/Submit`. The agent never writes Issue or FeedbackReport rows itself.

### Retention workflow
`AgentConversationRetentionJob` (daily 03:15 UTC) deletes `AgentConversation` rows older than `AgentSettings.RetentionDays` (default 90). Messages cascade-delete.

## Related Features

- Issues system (`docs/features/28-issues-system.md`) — handoff target via client-side modal pre-fill.
- Legal documents (`docs/features/legal-documents.md`) — `agent-chat` slug renders the AI Terms at `/Legal/agent-chat`; linked from the Assistant panel composer footer.
- GDPR export (`docs/features/gdpr-export.md`) — conversation/message data included.
