---
name: no-chained-bash-commands
description: HARD RULE. Never chain commands in one Bash call (&&, ;, loops, subshells) — a compound command doesn't match the allowlist and falls back to manual approval every time.
---

**HARD RULE.** One simple, trivially classifiable command per Bash call. No `&&`, no `;`, no `||`. Also banned: `for`/`while`/`if` constructs, shell variable assignment (`VAR=x cmd`), `bash -c`/`sh -c` wrappers, heredocs, subshells as the command, multi-line scripts. Prefer a dedicated tool over a shell command whenever one exists — Read over `cat`, Grep over `grep`, Glob over `find`, Write/Edit over redirection; those never reach the classifier at all.

**Why:** a compound command does not match the allowlist pattern at all — `for f in ...; do git add $f; done` isn't a `git add` command as far as `Bash(git add:*)` is concerned. It falls through to the auto-mode classifier, which can't rule on a complicated construct, and **falls back to Peter for manual approval every single time**. Peter: "it's more about the action being a complicated loop that the auto mode classifier borked on." Adding allowlist entries doesn't fix this; simplifying the command does. On an unattended or background run this parks the whole lane until he notices — it's not "annoying but slower," it kills progress.

**How to apply:** one command per Bash call, no exceptions. The working directory persists between calls, so `cd` is its own call and everything after it is its own call. Independent commands can go in the same message as parallel tool uses; dependent ones go in sequence. Reserve a single call only for cases where the shell genuinely needs one process (a heredoc, a pipeline that IS the command). Put this rule in every subagent prompt — a worker that chains parks its own unattended run.

Related: [[never-use-git-dash-c]] — same root cause, allowlist prefixes.
