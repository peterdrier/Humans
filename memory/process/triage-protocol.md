---
name: Triage protocol — full history, verbatim text, spec changes need Peter's review
description: When running `/triage` (or any feedback-handling work), fetch the full message thread for every report, show the reporter's verbatim Description text alongside any analysis, and stop the autonomous pipeline on any feedback-originated request that proposes a behavioral/policy/capability/spec change beyond a mechanical fix.
---

`/triage` and any other feedback-handling work follow three coupled rules. They fire together because each individually fails without the others — full history without verbatim hides reporter nuance; verbatim without classification ships spec changes Peter never sanctioned; classification without history misses Peter's manual replies.

## 1. Fetch full message history for every report

`GET /api/feedback/{id}/messages` for **every** report being triaged — not just the ones the list endpoint flags as having messages. The list endpoint's `messageCount` / `lastAdminMessageAt` can be stale or lag behind recent activity.

**Why it matters:** Peter may have responded to a report manually between the list fetch and the triage session. Drafting a fresh response without checking leads to:
- Duplicate messages to the reporter (unprofessional)
- Re-asking for info Peter already asked for
- Missing that Peter already resolved the issue conversationally
- Wasted effort drafting responses that won't be sent

In Phase 2 of `/triage`, right after fetching the feedback list, loop through ALL open/acknowledged reports and fetch their messages. Note for each report whether admin has already replied, when, and with what. Only draft new responses for reports with no prior admin message, or where the reporter has replied since the last admin message. Same rule for close-phase notifications — don't blindly send a "shipped!" message if someone already responded in that thread.

## 2. Show reporter's verbatim Description text alongside analysis

In `/triage` output, always include the reporter's full verbatim `Description` text — not just a one-line summary. The summary + analysis reflect interpretation, which can be wrong, and Peter needs the ground truth to steer the decision.

A one-line summary often hides nuance (tone, specifics, emotional register) the analysis can't capture. Keep the analysis / proposed-action columns alongside, but never substitute summary for the original text.

## 3. User-feedback spec changes need Peter's review before autonomous execution

When an issue originated from a user-submitted feedback report (i.e., flowed through `/triage`, came in via a feedback form, was filed by a non-Peter author based on a user's complaint), classify the proposed change before letting it enter the sprint or batch pipeline:

- **Mechanical fix** — improves an existing experience without changing what the system *does* or *allows*. Examples: better error message wording, fixing a typo, repairing a broken link, fixing a layout glitch, hiding a stack trace, restoring a missing icon. These can flow through `/triage` → issue → sprint → autonomous execution normally.

- **Spec change** — alters what the system does, who can do it, what data is collected/shown, or what policy applies. Examples: granting users a new capability, changing a default privilege tier, changing what fields are visible, adding/removing a workflow step, changing eligibility rules, adding a new public endpoint. These MUST be reviewed by Peter before any planning, batching, or dispatch.

If unsure which bucket a report falls into, treat it as a spec change. The cost of routing a borderline case to Peter is a one-line ask; the cost of treating a spec change as mechanical is shipping behavior the user requested but the project owner never sanctioned.

**Why:** PR #398 (2026-05-03) shipped because a user asked "I can't delete Drive files, please give me delete permission." Triage normalized that into a clean bug issue ("Drive permissions grant 'writer' not 'fileOrganizer'"); the spec read like a mechanical default-value bump; the autonomous pipeline (sprint → execute-sprint → batch worker) shipped a PR that granted every team member move/delete on shared Drive content. The user got what they asked for; the project owner did not get to decide whether that was the right policy. The breakdown wasn't any single layer's fault — it was that every layer treated the user's request as a fact rather than as a proposal.

The distinction matters because users legitimately surface real bugs ("the error message is unhelpful", "this link 404s"), and those should flow fast. The trap is that *spec change requests look identical to bug reports* once they've been triaged into a "fix this" issue body. Classification has to happen at triage time, with the original wording in front of you — which is why rule #2 (verbatim text) feeds rule #3 (classification).

**How to apply:**

- During `/triage`: when reading a user-submitted report, ask *did this user request a behavioral change, or did they report a broken experience?* If behavioral, do NOT autopromote to a clean bug issue with a tidy spec — surface the original verbatim text to Peter, label the issue as needing his review, and stop the pipeline there.
- During `/sprint` and `/execute-sprint`: before batching or dispatching an issue that originated from user feedback, re-check the original report. If the proposed change goes beyond a mechanical fix, escalate to Peter and skip from autonomous execution.
- Batch workers: if you read an issue spec derived from a user feedback report and it proposes a spec change, STOP, do not implement, report back to the orchestrator, and let Peter decide.

This rule does NOT block Peter-authored issues — Peter authoring an issue IS his approval for that change. It targets the laundering chain where a user's request becomes a spec via triage normalization.

**Related:** [`privilege-changes-need-explicit-approval`](privilege-changes-need-explicit-approval.md) — narrower rule: privilege/permission/role changes always need explicit approval, even when Peter authored the issue. [`issue-fetch-protocol`](issue-fetch-protocol.md) — hook-enforced rules for the broader "before implementing any GH issue" path.
