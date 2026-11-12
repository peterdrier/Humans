---
name: pr-review-feedback-handling
description: "Handling PR review feedback: fetch comments from BOTH repos, reply per-thread, resolve dispositioned threads, react 👍/👎 on Codex findings, never re-ping `@codex review`."
---

The mechanics of working review findings (Codex, Claude bot, humans). Whether a finding is real is
[`review-finding-triage`](review-finding-triage.md).

1. **Fetch everything.** Top-level comments, inline review comments and formal reviews are three
   separate fetches; the default PR view returns only the first. Check both `peterdrier/Humans` and
   `nobodies-collective/Humans`: bots sometimes post on the other repo.
2. **Reply in each finding's own thread** with a one-line disposition and the fix sha. A top-level
   summary may sit on top of thread replies, never instead of them. A finding restated on a later commit
   gets a reply in both threads.
3. **Resolve the thread in the same step as the reply**, whatever the disposition: fixed, refuted,
   wontfix, issue opened. The only thread left open is one still waiting on someone.
4. **React on every Codex finding** (reactions on the review comment itself): 👍 when valid, and also
   when correct but deliberately not changed; 👎 only when its claim about the code is false. That
   reaction is the signal that tunes what Codex flags.
5. **Never re-request a review after a fix push.** A new PR gets one `@codex review` comment when it
   opens; after that, no `@codex review` and no "please re-review". The Claude review reruns on every
   push by itself and Codex quota is limited.
6. **Findings posted as a top-level comment have no thread** (including your own review output). After
   fixing them, post a follow-up comment: what was fixed and the sha, what was deliberately not changed
   and why, and any false positives.

**Why:** unanswered or unresolved threads make a worked PR look unworked, and nobody can tell which
findings were handled.
