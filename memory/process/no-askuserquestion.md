---
name: no-askuserquestion
description: HARD RULE. Never use the AskUserQuestion tool in this project — ask questions inline in regular text instead.
---

**HARD RULE.** Never use `AskUserQuestion` in this project. Ask inline in regular text instead.

**Why:** Peter has called this out multiple times. The tool format adds friction — terse labels lose context, big option descriptions drain attention, and the chip/popup format interrupts flow when plain prose would work. He prefers inline questions where context lives directly in the question.

**How to apply:** When you need a decision from Peter, write the question as normal markdown text — embed all the context inline, list options as bullets, and let him reply in chat. Do not invoke `AskUserQuestion` even for genuine multi-option triage decisions. The allowance for those in `docs/architecture/peters-working-rules.md` § General Rules is Peter's cross-project default; this atom is his Humans-specific override of it, and the more specific rule wins.
