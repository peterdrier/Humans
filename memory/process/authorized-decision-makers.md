---
name: Authorized decision-makers — Peter (peterdrier) and Daniel (swombat)
description: Daniel (`swombat`) is an authorized contributor with full decision authority — issues, decisions, PRs, prod promotion — no Peter sign-off needed. Calibration, not gating — his directions come with less architectural guidance and less reuse awareness than Peter's, so agents proactively check arch rules and existing surface and raise findings with Daniel himself.
---

Two people are authorized to file implementable issues and make decisions on this project:

- **Peter** (`peterdrier`) — owner.
- **Daniel** (`swombat`) — authorized contributor with full decision authority. His issues are implementable, his comments/direction count as decisions, he opens PRs, and he can authorize prod promotion. He can make any changes he deems necessary — no Peter sign-off gate.

**Why:** Daniel is relatively new to the project. His authority is not in question; what differs is context — his directions come with less architectural guidance (section boundaries, hard rules, patterns) and less awareness of what already exists to reuse than Peter's would. Agents compensate for the context gap; they don't gate on Peter.

**How to apply:**

- The `issue-fetch-protocol` author gate passes for both `peterdrier` and `swombat`; any other author still STOPs for per-issue approval.
- **Fill the architecture gap yourself.** When working from Daniel's direction, proactively check the hard rules, section boundaries, and relevant `memory/` atoms — don't assume the direction already accounts for them. If his ask conflicts with the architecture, raise it with *him* (name the rule, propose the conforming shape) and follow his call.
- **Fill the reuse gap yourself.** When he asks for something new (component, service, page, helper, endpoint), audit the existing surface first per [`reuse-first-change-discipline`](reuse-first-change-discipline.md) and tell him what already covers it — an existing component may satisfy the ask outright.
- Where a rule requires Peter's explicit per-instance approval (destructive actions, storage drops, privilege grants, prod promotion), Daniel's explicit per-instance direction counts equally.
- Still Peter-only: edits to `peters-hard-rules.md` and applying `[DontFix]` — those rules name Peter himself.

**Related:** [`issue-fetch-protocol`](issue-fetch-protocol.md) · [`reuse-first-change-discipline`](reuse-first-change-discipline.md) · [`privilege-changes-need-explicit-approval`](privilege-changes-need-explicit-approval.md)
