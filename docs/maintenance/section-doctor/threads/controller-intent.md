# Controller intent

Runs as a subagent, `opus low` (`doctor-reader`).

Every method in every controller and view component the section owns, actions and private helpers
alike, read for intent, not size. HUM0031 (fixed at 40/15) only catches the worst offenders; a short
method can still make a domain decision.

**The line: a controller may change how a fact looks, never what the fact is.** A fact is anything
the rules produce about an object: tax owed, a total, eligibility, the next status, whether this
user may edit it. Presentation is how it is shown: order, subset, format, units, time zone, layout.
Sorting, filtering, formatting and time-zone conversion for display are fine in a controller;
working out the tax on a store item is not.

Tie-breaker: **would every surface have to agree on it?** The page, an email, an export, an API. Tax
owed must match on the page, the receipt and the invoice: a fact, so the service computes it. Sort
order and time zone may differ by page or viewer: presentation, so the controller may.

Two edges the line settles:

- **Filtering for display is presentation; filtering to hide what the viewer may not see is
  authorization**, a fact, and belongs in the service.
- **Asking a fact is fine; computing it is not.** "Can edit?" is read from the service, never
  worked out in the controller beside the service's own check.

Look for, each as its own check:

1. **Read path that writes.** A GET action or view component that calls a write or mutating member.
   HUM0038 already fails the build for `I*Write` members and `[ExternalWrite]` methods; look for the
   rest (mutating `I*Service` / `I*Management` members, writes reached through a helper).
2. **Post-mutation sequencing.** An action that calls a mutation, then decides which other domain
   calls follow (notifications, sync, compensation). Which consequences follow a change is a fact.
3. **Computed fact.** A total, price, eligibility or status predicate (`CanEdit`, `CanWithdraw`,
   status sets) worked out in the controller, beside or instead of the service's own rule.
4. **Same fact twice.** One predicate or rule written in two actions of the same controller.
5. **Fact the DTO should carry.** A controller combining several reads into a flag or value that one
   computed property on the canonical read DTO would carry.
6. **Domain exception steering.** A catch that decides a domain outcome rather than a response.

Each finding: `file:line`, which check, `clear` or `borderline`, the owning service, and the smallest
fix (a guard inside an existing service method, or one computed DTO property, before any new
interface member; new public surface is a Needs-Peter item). A status-to-badge mapping is
presentation, not a finding. Never propose moving 40/15, and never propose splitting a method into
controller-local helpers: that dodges the metric and moves no decision.
`docs/debt/controller-intent-audit-2026-06-12.md` is the worked example.
