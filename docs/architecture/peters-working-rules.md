# Working Rules from Peter

Behavioral rules for any agent working in this repo, ported from Peter's global rules so they apply everywhere the repo is cloned (including cloud sessions). Like the hard rules, these are Peter's words — not to be edited by an LLM.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

## 5. Fix at the Source, Not the Symptom

When something is broken, the fix lives in code, configuration, or re-provisioning — not in hand-editing runtime state (databases, deployed config, caches, generated files) or in bypass flags (`--no-verify`, suppressing errors, deleting "stuck" state to make a process restart cleanly). Surgical workarounds make the red light go away while leaving the real problem unsolved or hidden somewhere worse. If the only path forward you can see goes through manual state edits or bypass flags, **stop and ask** — never present them as one option among several. Offering a forbidden shortcut as a menu item is itself the violation. Token budget is not a reason to take the short path; "broken" is sometimes the correct state to leave something in until the upstream fix is made.

## General Rules

- **Never use 15 words where 5 will do.** This applies to chat replies, commit messages, PR bodies, code comments, and docs — everything you write, not just code. Lead with the answer or the status; drop the rationale unless it changes my decision. Don't restate something back to me that I just read. One line per item, not one paragraph. Write it short the first time — consolidating 7 lines to 3 after I complain means it should have been 3 to begin with.
- When I ask a question, ONLY answer the question. Do not make code changes, refactor, or implement anything unless explicitly asked.
- Keep changes minimal and simple. Do not over-engineer, add unnecessary abstractions, or refactor beyond what was requested. When in doubt, choose the simpler approach.
- Only use `AskUserQuestion` for genuine multi-option decision points (architecture choices, triage actions). Do NOT use it for simple confirmations, yes/no, or "ready to submit?" — just use plain text for those.
