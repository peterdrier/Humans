---
name: issues-need-section
description: Every GitHub issue must state which section it belongs to — a `section:{name}` label, or an explicit "Section: TBD" if genuinely unknown. Never omit silently.
---

Every GitHub issue must explicitly state which section of the app it belongs to. Either apply the appropriate `section:{name}` label, or — if genuinely unable to determine the section — state `**Section:** TBD` in the issue body so it's visible and reviewable. Never submit an issue with no section indication at all.

**Why:** Peter uses section grouping to plan sprints and understand the backlog. A silently-missing section forces him to re-analyze the issue to place it. A wrong guess is worse than an explicit TBD because it hides the uncertainty.

**How to apply:** During issue drafting, always include a section label in the proposed labels list. If no existing section label fits (the issue spans many sections, or creates a new area), don't silently fall back to a generic label or skip it — put `**Section:** TBD` in the body and flag the ambiguity so Peter can decide.
