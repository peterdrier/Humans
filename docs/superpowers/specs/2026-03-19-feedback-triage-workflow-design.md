# Feedback Triage Workflow — Design Spec

**Issue:** #147 — Add feedback-to-issue triage workflow via API
**Date:** 2026-03-19
**Supersedes:** #142 (bug reporting link — covered by feedback widget + this workflow)

## Context

The in-app feedback system (#141) is live. Humans submit bug reports, feature requests, and questions via a widget on every page. Reports are stored in the database and accessible through both an admin web UI (`/Admin/Feedback`) and a JSON API (`/api/feedback`).

The API already supports all CRUD operations needed for triage:

| Endpoint | Purpose |
|----------|---------|
| `GET /api/feedback?status=Open` | List pending reports |
| `GET /api/feedback/{id}` | Get single report |
| `PATCH /api/feedback/{id}/status` | Update status |
| `PATCH /api/feedback/{id}/notes` | Set admin notes |
| `PATCH /api/feedback/{id}/github-issue` | Link GH issue number |
| `POST /api/feedback/{id}/respond` | Send email response to reporter |

Authentication: `X-Api-Key` header against `FEEDBACK_API_KEY` env var.

**Gap:** No workflow ties these endpoints together. Feedback accumulates without being acted on. The goal is a Claude Code workflow where pending feedback surfaces during dev sessions and can be triaged interactively.

## Deliverables

### 1. `/whats` skill integration

Add a feedback check to the `/whats` skill's Step 1 (parallel gathering).

**When:** All modes except `next` (too slow for single-line output).

**How:**
```bash
curl -sf -H "X-Api-Key: $HUMANS_API_KEY" "$HUMANS_API_URL/api/feedback?status=Open"
```

**In output:**
- Status line: "X pending feedback reports" alongside open issue/blocked counts
- If any Open feedback exists, add to recommendations: "Run `/triage` to process N pending feedback reports"
- Feedback count is informational only — `/whats` does not triage, it surfaces

**Env vars** (stored in `.claude/settings.local.json`, gitignored):
- `HUMANS_API_URL` — defaults to production (`https://humans.nobodies.team`)
- `HUMANS_QA_API_URL` — QA instance (`https://humans.n.burn.camp`)
- `HUMANS_API_KEY` — shared API key for both environments

### 2. `/triage` skill (new)

Interactive skill that processes pending feedback one at a time.

**Trigger:** User runs `/triage` (or recommended by `/whats` when feedback is pending).

**Arguments:**
- *(none)* — triage Open reports from production
- `qa` — triage from QA instance instead
- `all` — include Acknowledged reports (re-triage)

**Flow:**

1. **Fetch** `GET /api/feedback?status=Open` (for `all` mode: two requests — one for Open, one for Acknowledged — merged client-side, since the API accepts only a single status filter)
2. **If empty** — "No pending feedback." — done
3. **For each report**, display:
   - Category badge (Bug / Feature Request / Question)
   - Description (full text)
   - Page URL where submitted
   - Submission date
   - Reporter name (from API response)
4. **Action menu** via `AskUserQuestion`:

| Action | Steps |
|--------|-------|
| **Respond & Resolve** | User writes message → `POST /respond` → `PATCH /status` to Resolved |
| **Create Issue** | `gh issue create --repo nobodies-collective/Humans` → `PATCH /github-issue` → `PATCH /status` to Acknowledged |
| **Create Issue + Respond** | Create issue, then send response mentioning issue number, then Acknowledged |
| **Won't Fix** | Optional response → `PATCH /status` to WontFix |
| **Skip** | Move to next report |

5. **Summary** — "Triaged X reports: Y issues created, Z responses sent, W skipped"

**Issue creation details:**
- Title: AI summary of first sentence of description, or user-provided
- Body template:
  ```markdown
  **Reported via in-app feedback**

  > {description}

  - **Category:** {category}
  - **Page:** {pageUrl}
  - **Reported:** {createdAt}
  ```
- Labels: `bug` for Bug, `enhancement` for FeatureRequest, `question` for Question
- After creation: link issue number back to feedback report via `PATCH /github-issue`

**Response drafting:**
- For each response action, Claude drafts a message based on the feedback content and action taken
- User reviews/edits via `AskUserQuestion` text input before sending
- Example draft for issue creation: "Thanks for reporting this! We've logged it as #{number} and will look into it."

### 3. Admin/Configuration page — add Feedback API key

Add `FEEDBACK_API_KEY` to the Admin/Configuration diagnostics page so admins can verify it's set on production.

**Change:** In `AdminController.Configuration()`, add an env var check after the existing Ticket Vendor pattern:

```csharp
var feedbackApiKey = Environment.GetEnvironmentVariable("FEEDBACK_API_KEY");
items.Add(new ConfigurationItemViewModel
{
    Section = "Feedback API",
    Key = "FEEDBACK_API_KEY (env)",
    IsSet = !string.IsNullOrEmpty(feedbackApiKey),
    Preview = !string.IsNullOrEmpty(feedbackApiKey)
        ? feedbackApiKey[..Math.Min(3, feedbackApiKey.Length)] + "..."
        : "(not set)",
    IsRequired = false,
});
```

This follows the exact pattern used for `TICKET_VENDOR_API_KEY` at line 175-183.

## Environment Setup

### Server side
- `FEEDBACK_API_KEY` env var set in Coolify for QA and production deployments
- Same key value used for both (simplifies Claude Code config)

### Claude Code side
`.claude/settings.local.json` (gitignored):
```json
{
  "env": {
    "HUMANS_API_URL": "https://humans.nobodies.team",
    "HUMANS_QA_API_URL": "https://humans.n.burn.camp",
    "HUMANS_API_KEY": "<key>"
  }
}
```

## YAGNI — Not included

- **Webhook notifications** — no push notifications when feedback arrives; polling via `/whats` is sufficient
- **Batch triage** — no bulk actions; at ~500 users, volume is low enough for one-at-a-time
- **Priority scoring** — no automatic prioritization of feedback; human judgment during triage
- **Feedback history for reporters** — reporters don't see their past submissions (beyond email responses)
- **Auto-categorization** — no AI classification of feedback category; reporters choose

## Files to create/modify

| File | Action | Purpose |
|------|--------|---------|
| `~/.claude/skills/triage/SKILL.md` | Create | New triage skill |
| `~/.claude/skills/whats/SKILL.md` | Modify | Add feedback check to Step 1 |
| `src/Humans.Web/Controllers/AdminController.cs` | Modify | Add FEEDBACK_API_KEY to config page |
| `docs/features/27-feedback-system.md` | Update | Document triage workflow integration |
