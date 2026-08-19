---
name: Cancellation-token propagation into external API calls
description: An outbound call that MUTATES a third-party system never receives a request-scoped token (HttpContext.RequestAborted / a controller action CancellationToken). Read-only outbound GETs may keep it. Three-way distinction — request-scoped vs process-lifetime vs genuine caller cancellation. Enforced by HUM0033 + [ExternalWrite].
---

Never hand a **request-scoped** cancellation token to an outbound call that
writes to a third-party system. Read-only outbound fetches may keep it.
Forwarding a token because "the parameter is there" is not a decision — pick
one of the three cases below deliberately at every boundary.

**Why:** A vendor sync must finish once started, regardless of whether the human
who triggered it is still watching. `HttpContext.RequestAborted` fires when a
user closes the tab, hits back, or navigates away — none of which mean "undo the
half of the Google Workspace reconcile you already applied." A local DB write
torn mid-flight rolls back; a remote write torn mid-flight leaves Workspace,
TicketTailor, or Holded in a state we have no record of and no compensation for.
The concrete incident: `GoogleController.SyncExecute` / `SyncExecuteAll` drove
admin-triggered reconciles straight from `RequestAborted`, so closing the tab
during a long sync stranded group memberships and Drive permissions partly
applied (nobodies-collective/Humans#950; split from #946).

## The three kinds of token

| Kind | Source | Rule |
|---|---|---|
| **Request-scoped** | `HttpContext.RequestAborted`, a controller action's `CancellationToken` parameter, `ViewComponent`/filter `HttpContext.RequestAborted` | The user gave up watching. **Must not** abort an external call that mutates remote state. Fine to honour for read-only fetches populating a view nobody is looking at any more. |
| **Process-lifetime** | Hangfire's job token, `IHostedService` stopping token, host shutdown | The server is going down. Honour it at safe boundaries — between items in a loop, before starting the next unit of work — but never let it interrupt a partially-applied remote mutation. Check it, don't thread it into the mutating call. **In this repo it does not currently occur on Hangfire paths** — see below. |
| **Genuine caller cancellation** | An explicit abort affordance the user operated ("Cancel this sync") | Honour it. **No such affordance exists in the UI today**, so no call site is currently in this category. Don't retrofit a request-scoped token and call it caller cancellation. |

**How to apply:**

- **Default for any outbound call that writes to a third-party system: pass
  `CancellationToken.None`.** Say why in a comment at the boundary, the way
  `AccountController.cs` does above `CompleteExternalLoginAsync`.
- **Fix it at the entry point, not in the client.** The hazard is introduced
  where a request-scoped token is first handed to a write-capable service. Do
  not thread a second "safe" token through the service layer, and do not have
  clients silently ignore the token they were given — a client that accepts a
  `CancellationToken` must honour it.
- **Read-only outbound GETs keep the token.** Preview/list/lookup paths
  (`SyncAction.Preview`, `MyTicketStubsViewComponent`,
  `TicketHoldingsViewComponent`, `UserCalendarViewComponent`) are correct as
  written — abandoning a read for a page nobody is viewing is the desired
  behaviour.
- **Long-running admin writes belong in Hangfire.** Enqueueing with
  `CancellationToken.None` (`TicketController.Sync`) removes the question
  entirely. Prefer it whenever the UI does not need the result inline.
- **A write-capable service method is write-capable at every call site**, even
  when a `SyncAction.Preview`-style argument makes one particular call read-only.
  The verb of the HTTP action is what settles it: a `[HttpGet]` action may pass a
  request-scoped token; a `[HttpPost]`/`[HttpPut]`/`[HttpDelete]`/`[HttpPatch]`
  action may not.

**Hangfire jobs are already non-cancellable here — don't reason as if they
aren't.** Every job registration bakes a *literal* `CancellationToken.None` into
the enqueue expression: all 21 `RecurringJob.AddOrUpdate<…>(… ExecuteAsync(
CancellationToken.None) …)` registrations plus every ad-hoc `Enqueue`. That
falls out of [`hangfire-method-signature-stable`](../code/hangfire-method-signature-stable.md),
which requires passing every parameter explicitly at the enqueue site so the
serialized `MethodInfo` stays pinned. The consequence: the process-lifetime row
above is a *category*, not a live case — a job's `CancellationToken` parameter is
always `None` at runtime, so a Workspace/Holded/MailerLite write on a job path
cannot be torn by shutdown either. Before "honouring the job token at safe
boundaries" anywhere, check that a real token is actually being passed; today
none is.

**A paid outbound query is not a write.** An outbound call that mutates nothing
remote but costs money (`IGoogleTranslationClient.TranslateAsync`, billed per
character) stays cancellable: there is no partial remote state to strand, and
abandoning the loop early *saves* spend an admin who navigated away didn't want.
Weigh integrity first, cost second — cost alone never makes a call
`[ExternalWrite]`.

**Guard irreversible multi-step writes inside the service too.** Where step 1
commits something at the vendor and step 2 must follow, detach locally at that
seam (`var commitCt = CancellationToken.None;`) instead of trusting every future
caller — `TicketTransferService.WriteToVendorAsync` does this once the
TicketTailor void has committed. This is not "threading a second token": the
signature is unchanged and the detach is a one-line local at the commit point.

**Enforcement:** Mark Application-layer interface methods that reach an
external mutating call with `[ExternalWrite]`
(`Humans.Base.Attributes`). Analyzer **HUM0033** then errors when a
state-changing controller action passes a request-scoped token to one of them.
When you add a service method that reaches a new outbound write, mark it —
the analyzer is only as complete as the markers.

**Related:** [`universal-enforcement-over-per-section`](universal-enforcement-over-per-section.md) ·
[`analyzer-exceptions-via-attributes`](analyzer-exceptions-via-attributes.md) ·
[`vendor-connectors-own-sections`](vendor-connectors-own-sections.md)
