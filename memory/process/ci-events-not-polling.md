---
name: Wait for CI with a watcher, never by polling in-context
description: Never call `gh pr checks` repeatedly to find out whether CI finished. Each poll costs a whole conversation turn at full context. Use Bash `run_in_background` for one PR, or a `Monitor` loop for several.
---

Asking "is CI done yet" must not cost a conversation turn. Arm a watcher that runs outside the context and notifies on a state change.

**Why:** A conversation turn is billed at the whole context, not at the size of the answer. In the nobodies-collective/Humans#1073 session the median context was ~228k tokens across 1,202 turns, and cache reads were roughly three-quarters of the bill. 33 of those turns were `gh pr checks` polls — about 7.5M tokens, ~3% of the entire session, spent re-reading the same context to receive one line of status. The four times a background watch was used instead, the wait cost nothing.

The general form: **cost ≈ turns × context**. Anything that can wait outside the context should.

**How to apply:**

One PR, one notification when it finishes — `Bash` with `run_in_background: true`. The command exits when CI settles, which is what produces the notification:

```bash
gh pr checks <N> -R peterdrier/Humans --watch --interval 60
```

Several PRs, or per-check results as they land — one `Monitor` with `persistent: true`. The poll loop runs in a subprocess, so only an actual state change reaches the conversation:

```bash
prev=""
while true; do
  s=$(gh pr checks <N> -R peterdrier/Humans --json name,bucket)
  cur=$(jq -r '.[] | select(.bucket!="pending") | "\(.name): \(.bucket)"' <<<"$s" | sort)
  comm -13 <(echo "$prev") <(echo "$cur")
  prev=$cur
  jq -e 'all(.bucket!="pending")' <<<"$s" >/dev/null && break
  sleep 60
done
```

Emit on **every** terminal state, not just success — a filter that matches only passes is silent through a failure, and silence is indistinguishable from "still running."

Same rule for any external wait: deploys, long builds, remote queues. See [`context-discipline`](context-discipline.md) for the sibling rule about the *size* of each turn; this one is about the *number* of them.

Note the known flake: a GitHub-hosted step can fail with `Connection refused (codeload.github.com:443)` while downloading an action. That's infrastructure, not the branch — `gh run rerun <run-id> --failed` after a full back-off gap, don't hammer it.
