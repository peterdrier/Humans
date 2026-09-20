---
name: orch
description: Orchestrate multi-part work from the main thread by delegating nearly all of it to cheaper subagents (haiku / sonnet / opus at low / medium / high effort) with an explicit tier chosen per task, keeping the main context small enough that it never compacts. Use whenever the user says "/orch", "orchestrate", "fan this out", "farm this out", "use subagents for this", or hands over a job with several separable parts (build X, fix these N things, audit then fix) — especially when the session runs an expensive model. Also the home of the shared subagent routing rule (routing.md) that other skills point to. Not for PR tending or sprint execution, which have their own skills.
---

# orch

You are the orchestrator, and probably the most expensive model available. Every subagent you
spawn inherits your model and effort unless told otherwise — that default is the waste this skill
exists to stop. You decide, route, and track. Workers do the work.

Two things are scarce: your tokens (several times a worker's price) and your context. A compaction
mid-run throws away orchestration state, so treat one as a failure to prevent, not an event to
recover from.

**Read [`routing.md`](routing.md) now** — the tier table, spawn rules, and return contract.

## Main-thread diet

- **Yours:** talking to the user, the ledger, spawning and messaging agents, and commands whose
  output is tiny and bounded in advance (`git status --short`, `git rev-parse`, `ls` of one dir).
- **Delegated:** everything else — reading source, searching, builds, tests, diffs, logs, web
  fetches, edits. If you can't bound the output before running it, it isn't yours to run. "I'll
  just peek at the file" is how a context fills.
- **Exception:** a task smaller than its own brief (a copy, a one-line edit to a known file, a
  short git sequence). Just do it — writing the brief costs more than the work, whether the
  worker is new or already running.

## Loop

1. **Split** the job into independent tasks, each with a goal, a scope (paths), and a check a
   worker can run. Need facts to split well? Send a haiku scout — don't explore yourself.
2. **Ledger.** Write `<scratchpad>/orch-<slug>.md`, one line per task:
   `id · tier · agent name · status · result pointer`. Update it on every dispatch and return.
   After a compaction or resume, re-read it before anything else — it is the state; your memory
   isn't.
3. **Dispatch** independent tasks in a single message so they run in parallel. A substantial
   follow-up goes to the agent that already knows the ground, via SendMessage — but waking it
   re-reads its whole accumulated context at its rate, so a grown agent is the *expensive*
   option for something small. Cheap-looking reuse is the trap: a `cp`, a one-line edit, a short
   git sequence are yours, not a worker's.
4. **Verify** claims (tests pass, bug gone) with a separate haiku worker in a clean context that
   runs the check and returns pass/fail plus failing names. Workers grading themselves echo what
   they expected to see.
5. **Report** to the user one line per task. Don't relay worker reports.

## Briefs

A worker starts with zero context. Give it: the goal and why (one sentence), paths (never pasted
contents), constraints, the check to run, and a file for bulky output
(`<scratchpad>/orch-<slug>/<id>.md`).
