---
name: ask
description: Surface everything this session is waiting on the user to answer or decide, with enough context to answer cold — without reading the session. Use whenever the user says "/ask", "ask", "what do you need from me", "what are you waiting on", "any questions for me", "catch me up", or returns to a long-running session after being away. Presume the user has NOT read this conversation, even if they were present earlier.
---

# Ask

The user runs many sessions in parallel — local, cloud, overnight. Sessions ask questions inline and wait for answers, but the user cannot read every session's scrollback. When this skill is invoked, produce **one self-contained message** that lets the user answer everything cold.

**Core rule: write for someone who has read none of this session.** No shorthand, codenames, lane letters, or numbering invented during the session. Name things concretely: file paths, PR numbers, branch names, section names, actual values. If you catch yourself writing "the second approach" or "Option B from earlier" — stop and spell it out.

This is reporting only. Do not start new work, apply fixes, or re-litigate settled decisions.

## Structure, in this order

### 1. Shortcuts and deviations — always first

Anything where you deviated from instructions, worked around a blocker instead of fixing it, skipped a step, left something broken or degraded, or delivered less than was asked. State it plainly, even if it looks bad. This section comes first because it changes how the user reads everything else — buried shortcuts are how bad state ships. If there are none, omit the section entirely (don't write "no shortcuts taken").

### 2. Questions waiting on an answer

Numbered list. For each item:

- **Background**: 2–4 plain sentences, fully self-contained. What the work is, where it stands, why this decision came up.
- **The question**, stated directly.
- **Options with a recommendation**, where genuine options exist. One sentence per option.

Only include questions that actually block or shape the work. Skip anything already answered earlier in the session. A question you could resolve yourself from code or docs isn't the user's to answer — omit it and list it as your own next step; don't do the digging during /ask (reporting only).

### 3. Assumptions made without asking

Decisions you made silently that the user might want to redirect. One line each: what you assumed, and what changes if it's wrong. If none, omit the section.

### 4. Nothing pending?

If there are no shortcuts, no open questions, and no notable assumptions: say so in one line, plus one line on current state. Done.

## Format rules

- Every item must be answerable **by number in one reply** ("1: yes, 2: the first one, 3: fine").
- Enough context to answer — not a session recap. If a sentence doesn't help the user decide, cut it.
- Plain language. No project jargon the user didn't introduce themselves.
