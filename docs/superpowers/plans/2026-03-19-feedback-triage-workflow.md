# Feedback Triage Workflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable Claude Code to surface pending feedback in `/whats` and triage it interactively via `/triage`.

**Architecture:** Three changes — one C# code addition (Admin config page), two skill files (modify `/whats`, create `/triage`). The API already exists; this is purely workflow integration.

**Tech Stack:** ASP.NET Core (C#), Claude Code skills (markdown), curl for API calls

**Spec:** `docs/superpowers/specs/2026-03-19-feedback-triage-workflow-design.md`

---

### Task 1: Add FEEDBACK_API_KEY to Admin Configuration page

**Files:**
- Modify: `src/Humans.Web/Controllers/AdminController.cs:183` (after line 183, before `return View`)

- [ ] **Step 1: Create feature branch**

```bash
git checkout -b feature/147-feedback-triage
```

- [ ] **Step 2: Add the env var check to AdminController.Configuration()**

In `src/Humans.Web/Controllers/AdminController.cs`, insert after line 183 (the closing `});` of the Ticket Vendor block) and before line 185 (`return View`):

```csharp
        // Feedback API key is from env var
        var feedbackApiKey = Environment.GetEnvironmentVariable("FEEDBACK_API_KEY");
        items.Add(new ConfigurationItemViewModel
        {
            Section = "Feedback API",
            Key = "FEEDBACK_API_KEY (env)",
            IsSet = !string.IsNullOrEmpty(feedbackApiKey),
            Preview = !string.IsNullOrEmpty(feedbackApiKey) ? feedbackApiKey[..Math.Min(3, feedbackApiKey.Length)] + "..." : "(not set)",
            IsRequired = false,
        });
```

This follows the exact pattern of lines 174-183 (Ticket Vendor API key).

- [ ] **Step 3: Build to verify no compilation errors**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/AdminController.cs
git commit -m "feat: add FEEDBACK_API_KEY to admin configuration page (#147)"
```

---

### Task 2: Update /whats skill to surface pending feedback

**Files:**
- Modify: `~/.claude/skills/whats/SKILL.md`

- [ ] **Step 1: Add feedback check to /whats tiered gathering table**

In the `### 1b: Tiered gathering` section, add a new row to the source table:

| Source | `next` | default | `blocked` | `new` | `full` |
|--------|--------|---------|-----------|-------|--------|
| Feedback API | — | yes | — | — | yes |

- [ ] **Step 2: Add feedback fetch instructions after Step 1c**

Add a new section `### 1d: Feedback check` after `### 1c: Batch GitHub data`:

```markdown
### 1d: Feedback check

When the feedback API check is applicable (see tiered gathering), fetch pending feedback in parallel with other sources:

\`\`\`bash
curl -sf -H "X-Api-Key: $HUMANS_API_KEY" "$HUMANS_API_URL/api/feedback?status=Open" 2>/dev/null
\`\`\`

If `HUMANS_API_URL` or `HUMANS_API_KEY` are not set, skip silently (no error). If the curl fails (server down, bad key), skip silently.

Count the number of Open reports from the JSON array response.
```

- [ ] **Step 3: Add feedback to output sections**

In `### Default (no modifier)`, add after the Status line:
```markdown
- If feedback API returned results, append to status: ", X pending feedback reports"
- If any Open feedback exists, add a recommendation: "Run `/triage` to process N pending feedback reports — community-reported issues should get fast turnaround"
```

In `### full`, add to the Status section:
```markdown
- Include pending feedback count in status line
- List each pending report briefly (category, first line of description, date)
```

- [ ] **Step 4: Commit (outside repo — this is a user skill file, no git)**

No commit needed — skill files live in `~/.claude/skills/`, outside the repo.

---

### Task 3: Create /triage skill

**Files:**
- Create: `~/.claude/skills/triage/SKILL.md`

- [ ] **Step 1: Create the skill directory**

```bash
mkdir -p ~/.claude/skills/triage
```

- [ ] **Step 2: Write the skill file**

Create `~/.claude/skills/triage/SKILL.md` with this content:

````markdown
---
name: triage
description: "Triage pending feedback from the Humans app — respond to reporters, create GitHub issues, update status. Use when /whats shows pending feedback or when you want to process community reports."
argument-hint: "[qa] [all]"
---

# Feedback Triage

Interactive triage of pending feedback from the Humans app via its API.

## Arguments

- *(none)* — triage Open reports from production
- `qa` — triage from QA instance (`$HUMANS_QA_API_URL`) instead of production
- `all` — include Acknowledged reports (for re-triage / follow-up)

## Prerequisites

Requires env vars (set in `.claude/settings.local.json`):
- `HUMANS_API_URL` — production base URL
- `HUMANS_QA_API_URL` — QA base URL
- `HUMANS_API_KEY` — API key (same for both environments)

If any required var is missing, tell the user and stop.

## Step 1: Fetch pending feedback

Determine base URL:
- If `qa` argument: use `$HUMANS_QA_API_URL`
- Otherwise: use `$HUMANS_API_URL`

Fetch Open reports:
```bash
curl -sf -H "X-Api-Key: $HUMANS_API_KEY" "$BASE_URL/api/feedback?status=Open"
```

If `all` argument, also fetch Acknowledged and merge:
```bash
curl -sf -H "X-Api-Key: $HUMANS_API_KEY" "$BASE_URL/api/feedback?status=Acknowledged"
```

Parse the JSON array. If empty → "No pending feedback." → done.

Store results. Sort by CreatedAt ascending (oldest first).

## Step 2: Triage each report

For each report, display:
```
### Feedback #{index} — {Category}
**Description:** {Description}
**Page:** {PageUrl}
**Submitted:** {CreatedAt}
**Reporter:** {ReporterName}
**Status:** {Status}
```

If the report has a screenshot URL, note it: "Screenshot available at {BASE_URL}{ScreenshotUrl}"

Then present the action menu via `AskUserQuestion`:

| Option | Label | Description |
|--------|-------|-------------|
| 1 | Respond & Resolve | Send a response to the reporter and mark as Resolved |
| 2 | Create Issue | Create a GitHub issue and link it back |
| 3 | Create Issue + Respond | Create issue, then send response mentioning issue number |
| 4 | Won't Fix | Mark as Won't Fix (optionally with a response) |
| 5 | Skip | Move to next report |

### Action: Respond & Resolve

1. Draft a response message based on the feedback content. Present it to the user for review/edit via `AskUserQuestion` with a text input option.
2. Send the response:
   ```bash
   curl -sf -X POST -H "X-Api-Key: $HUMANS_API_KEY" -H "Content-Type: application/json" \
     -d '{"message": "<response text>"}' \
     "$BASE_URL/api/feedback/{id}/respond"
   ```
3. Update status to Resolved:
   ```bash
   curl -sf -X PATCH -H "X-Api-Key: $HUMANS_API_KEY" -H "Content-Type: application/json" \
     -d '{"status": "Resolved"}' \
     "$BASE_URL/api/feedback/{id}/status"
   ```

### Action: Create Issue

1. Compose the issue title — summarize the description in one short sentence.
2. Create the GitHub issue:
   ```bash
   gh issue create --repo nobodies-collective/Humans \
     --title "<title>" \
     --label "<bug|enhancement|question>" \
     --body "$(cat <<'EOF'
   **Reported via in-app feedback**

   > {Description}

   - **Category:** {Category}
   - **Page:** {PageUrl}
   - **Reported:** {CreatedAt}
   EOF
   )"
   ```
3. Extract the issue number from the output.
4. Link it back to the feedback report:
   ```bash
   curl -sf -X PATCH -H "X-Api-Key: $HUMANS_API_KEY" -H "Content-Type: application/json" \
     -d '{"issueNumber": <number>}' \
     "$BASE_URL/api/feedback/{id}/github-issue"
   ```
5. Update status to Acknowledged:
   ```bash
   curl -sf -X PATCH -H "X-Api-Key: $HUMANS_API_KEY" -H "Content-Type: application/json" \
     -d '{"status": "Acknowledged"}' \
     "$BASE_URL/api/feedback/{id}/status"
   ```

### Action: Create Issue + Respond

1. Do "Create Issue" steps above.
2. Draft a response referencing the issue number, e.g.: "Thanks for reporting this! We've logged it as #<number> and will look into it."
3. Present to user for review/edit.
4. Send the response via the respond endpoint.
5. Status stays Acknowledged (not Resolved — issue is still open).

### Action: Won't Fix

1. Optionally draft a brief explanation response. Ask user if they want to send it.
2. If yes, send via respond endpoint.
3. Update status to WontFix:
   ```bash
   curl -sf -X PATCH -H "X-Api-Key: $HUMANS_API_KEY" -H "Content-Type: application/json" \
     -d '{"status": "WontFix"}' \
     "$BASE_URL/api/feedback/{id}/status"
   ```

### Action: Skip

Move to next report. No API calls.

## Step 3: Summary

After all reports are processed, print:

```
Triage complete: X reports processed
- Issues created: Y
- Responses sent: Z
- Won't Fix: W
- Skipped: S
```

If any issues were created, list them: `#number: title`
````

- [ ] **Step 3: Test that the skill loads**

Verify the skill appears in the skill list — the user can check by looking at the available skills in their next session (or by running `/triage` to see if it triggers).

- [ ] **Step 4: No commit needed**

Skill files live in `~/.claude/skills/`, outside the repo.

---

### Task 4: Update feature spec and commit

**Files:**
- Modify: `docs/features/27-feedback-system.md`

- [ ] **Step 1: Add triage workflow section to feature spec**

Append a section to `docs/features/27-feedback-system.md` documenting the triage workflow:
- Claude Code integration via API
- `/whats` surfaces pending feedback count
- `/triage` skill for interactive processing
- Environment setup requirements (FEEDBACK_API_KEY, Claude Code env vars)

- [ ] **Step 2: Commit**

```bash
git add docs/features/27-feedback-system.md docs/superpowers/specs/2026-03-19-feedback-triage-workflow-design.md docs/superpowers/plans/2026-03-19-feedback-triage-workflow.md
git commit -m "docs: add feedback triage workflow spec and plan (#147)"
```

---

### Task 5: Push and create PR

- [ ] **Step 1: Verify build passes**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 2: Run format check**

Run: `dotnet format Humans.slnx --verify-no-changes`
Expected: No formatting issues

- [ ] **Step 3: Push and create PR**

```bash
git push -u origin feature/147-feedback-triage
gh pr create --repo peterdrier/Humans --base main \
  --title "feat: feedback triage workflow (#147)" \
  --body "..."
```
