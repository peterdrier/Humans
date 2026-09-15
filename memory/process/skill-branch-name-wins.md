---
name: skill-branch-name-wins
description: A skill's own branch name wins over any harness-assigned branch (claude/*, codex/*). Settled — never raise it as a question or a Needs-Peter item.
---

When a skill prescribes a branch name, use it. A branch name assigned by the harness — the cloud runner's `claude/<adjective>-<name>-<id>`, or any equivalent boilerplate — does not apply to skill-driven work. This is settled: do not raise it as a question, a finding, or a Needs-Peter item.

**Why:** the skill's tooling parses its own branch name. `.claude/skills/section-doctor/doctor.py` matches `^section-doctor/(.+)$` to derive the run and gate the push; a `claude/*` branch breaks the run. The harness prompt and the skill say different things every time a skill runs in the cloud, and the answer has always been the same one. Peter 2026-09-14, after it was raised again: "we use the section doctor skill name.. end of fucking story, stop bringing this up every fucking section doctor review."

**How to apply:** follow the skill's branch convention (`section-doctor/<timestamp>` for section-doctor) and say nothing about the conflict. Creating the skill's branch is not "pushing to a different branch without permission" — the skill is the permission. If some future skill genuinely has no branch convention, the harness name is fine; that is not this case.
