---
name: user-originated feedback that changes a spec needs Peter's review
description: triggers when an issue originated from end-user feedback (triage→issue chain) and proposes a behavioral / policy / capability / spec change beyond a mechanical fix. Mechanical fixes (typos, broken links, unclear error messages, missing icons) can flow normally; spec changes need Peter's review before entering the autonomous pipeline.
---

When an issue originated from a user-submitted feedback report (i.e. flowed through `/triage`, came in via a feedback form, was filed by a non-Peter author based on a user's complaint), classify the proposed change before letting it enter the sprint or batch pipeline:

- **Mechanical fix** — improves an existing experience without changing what the system *does* or *allows*. Examples: better error message wording, fixing a typo, repairing a broken link, fixing a layout glitch, hiding a stack trace, restoring a missing icon. These can flow through `/triage` → issue → sprint → autonomous execution normally.

- **Spec change** — alters what the system does, who can do it, what data is collected/shown, or what policy applies. Examples: granting users a new capability, changing a default privilege tier, changing what fields are visible, adding/removing a workflow step, changing eligibility rules, adding a new public endpoint. These MUST be reviewed by Peter before any planning, batching, or dispatch.

If you're not sure which bucket a feedback report falls into, treat it as a spec change. The cost of routing a borderline case to Peter is a one-line ask; the cost of treating a spec change as mechanical is shipping behavior the user requested but the project owner never sanctioned.

**Why:** PR #398 (2026-05-03) shipped because a user asked "I can't delete Drive files, please give me delete permission." Triage normalized that into a clean bug issue ("Drive permissions grant 'writer' not 'fileOrganizer'"), the spec read like a mechanical default-value bump, and the autonomous pipeline (sprint → execute-sprint → batch worker) shipped a PR that granted every team member move/delete on shared Drive content. The user got what they asked for; the project owner did not get to decide whether that was the right policy. The breakdown wasn't any single layer's fault — it was that every layer treated the user's request as a fact rather than as a proposal.

The distinction matters because users legitimately surface real bugs ("the error message is unhelpful", "this link 404s"), and those should flow fast. The trap is that *spec change requests look identical to bug reports* once they've been triaged into a "fix this" issue body. The classification has to happen at triage time, with the original wording in front of you.

**How to apply:** During `/triage`, when reading a user-submitted report, ask: *did this user request a behavioral change, or did they report a broken experience?* If behavioral, do NOT autopromote to a clean bug issue with a tidy spec — surface the original verbatim text to Peter, label the issue as needing his review, and stop the pipeline there.

During `/sprint` and `/execute-sprint`, before batching or dispatching an issue that originated from user feedback, re-check the original report: if the proposed change goes beyond a mechanical fix, escalate to Peter and skip it from autonomous execution.

If you're a batch worker and you read an issue spec that derives from a user feedback report and proposes a spec change (capability grant, policy change, default change, new endpoint, eligibility change), STOP, do not implement, report back to the orchestrator, and let Peter decide.

This rule does NOT block Peter-authored issues — Peter authoring an issue IS his approval for that change. It targets the laundering chain where a user's request becomes a spec via triage normalization.

**Related:** [`privilege-changes-need-explicit-approval`](privilege-changes-need-explicit-approval.md) — narrower rule: privilege/permission/role changes always need explicit approval, even when Peter authored the issue. [`issue-no-non-peter-without-approval`](issue-no-non-peter-without-approval.md) — overlapping hook-enforced rule for non-Peter issue authors. [`triage-show-verbatim`](triage-show-verbatim.md) — the verbatim-text rule that gives `/triage` the data needed to classify correctly.
