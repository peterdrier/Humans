---
name: A subagent's claim about a call site's type needs a main-thread grep
description: A reading subagent naming the interface, type or signature at a call site is a hypothesis — re-grep on the main thread before striking or repairing anything on it.
---

When a reading subagent reports what type, interface or signature a call site uses, re-grep that
call site on the main thread before acting on the claim — whether you are deleting the symbol or
repairing prose about it.

**Why:** Reading agents work from a pre-fetched slice and cannot open the constructor, so they infer
a declared type from an identifier's *name*. On the 2026-09-18 Governance doctor run, a thread
reported that `Docs/Governance.md` named the wrong interface for a Notifications call site; the
local there is called `applicationDecisionService` and its actual declared type is
`IApplicationServiceRead`, so the doc was right and the finding was not. The same run's tests thread
assumed a service method had the same signature as the repository method behind it and proposed
deleting tests that covered the string-parsing the service does and the repository does not.

**How to apply:** The existing blast check already covers a symbol you are about to remove. This
extends it to a claim you are about to *repair*: a rewrite of prose is as wrong as a deletion if the
claim it replaces was true. Grep for the declaration, not the usage — the constructor parameter or
field, not the call. A claim that does not reproduce is declined as factually wrong, and the grep
that declines it often names the real defect nearby.

**Related:** [[review-finding-triage]]
