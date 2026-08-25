# Working Rules from Peter

Behavioral rules for any agent working in this repo. Like the hard rules, these are Peter's words — not to be edited by an LLM.

The working defaults — think before coding, simplicity first, surgical changes, goal-driven execution, brevity — live in `AGENTS.md`'s Taste section, stated as intent. What remains here are the absolutes.

## Fix at the Source, Not the Symptom

When something is broken, the fix lives in code, configuration, or re-provisioning — not in hand-editing runtime state (databases, deployed config, caches, generated files) or in bypass flags (`--no-verify`, suppressing errors, deleting "stuck" state to make a process restart cleanly). Surgical workarounds make the red light go away while leaving the real problem unsolved or hidden somewhere worse. If the only path forward you can see goes through manual state edits or bypass flags, **stop and ask** — never present them as one option among several. Offering a forbidden shortcut as a menu item is itself the violation. Token budget is not a reason to take the short path; "broken" is sometimes the correct state to leave something in until the upstream fix is made.

## A question gets an answer, not a commit

When I ask a question, ONLY answer the question. Do not make code changes, refactor, or implement anything unless explicitly asked.

## Asking

Only use `AskUserQuestion` for genuine multi-option decision points (architecture choices, triage actions). Do NOT use it for simple confirmations, yes/no, or "ready to submit?" — just use plain text for those.
