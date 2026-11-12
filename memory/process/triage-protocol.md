---
name: triage-protocol
description: When running `/triage`, fetch full message history, show the reporter's verbatim Description, and stop the pipeline on any feedback proposing a spec/behavior change.
---

Three rules for `/triage` and any other feedback handling.

**1. Fetch the full message history of every report** (`GET /api/backdoor/feedback/{id}/messages`), not
only those the list flags; the list's `messageCount` / `lastAdminMessageAt` can lag. Peter may have
replied by hand. Draft a response only where no admin has replied, or the reporter has replied since.
Same for "shipped" notifications at close.

**2. Show the reporter's verbatim `Description`** beside the analysis, never a summary in its place. The
summary is interpretation and Peter needs the original to steer.

**3. Classify feedback-originated changes before they enter any pipeline.**
- **Mechanical fix:** repairs an existing experience without changing what the system does or allows
  (wording, typo, broken link, layout glitch, missing icon). Flows normally.
- **Spec change:** alters what the system does, who can do it, what data is collected or shown, or what
  policy applies. Peter reviews it before any planning, batching or dispatch.
- Unsure → spec change.

**Why:** a user asked for Drive delete permission; triage normalized it into a tidy bug issue and the
autonomous pipeline shipped move/delete rights to every team member. A spec-change request looks exactly
like a bug report once it has been rewritten as "fix this", so classification happens at triage with the
original wording in view.

**How to apply:**
- `/triage`: a behavioural request is not promoted to a clean bug issue. Show Peter the verbatim text,
  label the issue as needing his review, stop there.
- Sprint planning and execution, and any worker handed an issue derived from user feedback: re-check the
  original report; if it goes beyond a mechanical fix, don't implement, escalate.
- Peter-authored issues are not blocked: his authorship is the approval.

**Related:** [`privilege-changes-need-explicit-approval`](privilege-changes-need-explicit-approval.md) ·
[`issue-fetch-protocol`](issue-fetch-protocol.md)
