---
name: cancellation-token-propagation
description: Never pass a request-scoped `CancellationToken` into an outbound call mutating a third party — read-only GETs may keep it. Hangfire job tokens are live process-lifetime tokens. HUM0033 + `[ExternalWrite]` enforce this.
---

Never hand a **request-scoped** cancellation token (`HttpContext.RequestAborted`, a controller action's
`CancellationToken`) to an outbound call that writes to a third-party system. Read-only fetches keep it.

**Why:** closing the tab does not mean "undo half of the Google Workspace reconcile". A torn local write
rolls back; a torn remote write leaves Workspace, TicketTailor or Holded in a state we have no record of
and no compensation for.

**How to apply:**
- **Outbound write → `CancellationToken.None`**, with a comment at the boundary saying why.
- **Fix it at the entry point**, where the request token is first handed to a write-capable service.
  Don't thread a second "safe" token through the services, and don't have a client ignore a token it
  accepted.
- **Read-only outbound GETs keep the token** (previews, lists, view components): abandoning a read
  nobody is watching is the point.
- **The HTTP verb settles it.** A write-capable service method is write-capable at every call site, even
  when an argument makes one call a preview: `[HttpGet]` may pass the request token,
  `[HttpPost]`/`[HttpPut]`/`[HttpDelete]`/`[HttpPatch]` may not.
- **Long-running admin writes belong in Hangfire**, enqueued with `CancellationToken.None`.
- **Hangfire job tokens are live:** Hangfire 1.8.25 (`CoreBackgroundJobPerformer.SubstituteArguments`)
  replaces every queued `CancellationToken`, literal `None` included, with the server's shutdown token.
  Treat it as process-lifetime: keep it on reads, check it between units of work, and finish a started
  remote mutation (and its local bookkeeping) with `CancellationToken.None`.
- **A paid query is not a write.** A call that costs money but mutates nothing remote (translation) stays
  cancellable. Integrity first, cost second.
- **Irreversible multi-step writes detach inside the service too:** once step 1 has committed at the
  vendor, use a local `CancellationToken.None` for the rest
  (`TicketTransferVendorExchange.WriteToVendorAsync`).
- There is no user-operated "cancel this sync" affordance today, so no call site may claim genuine
  caller cancellation.

**Enforcement:** mark service interface methods that reach an external mutating call with
`[ExternalWrite]` (`Humans.Base.Attributes`); analyzer **HUM0033** errors when a state-changing action
passes a request-scoped token to one. A new outbound write needs the marker.

**Related:** [`vendor-connectors-own-sections`](vendor-connectors-own-sections.md)
