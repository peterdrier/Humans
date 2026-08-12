---
name: hangfire-method-signature-stable
description: Methods invoked through Hangfire (`backgroundJobs.Enqueue<I>(...)` / `.Schedule<I>(...)`) need a frozen serialization signature — pin the call site to a no-defaults overload, and never add/reorder/change parameter types on that overload
---

A method bound by `IBackgroundJobClient.Enqueue<TInterface>(expression)` / `.Schedule<TInterface>(expression, ...)` is captured as a specific `MethodInfo` and serialized as `(ParamType1, ParamType2, ...)` in Hangfire storage. At dequeue time Hangfire does **exact** signature matching against the live assembly. Any change to that signature — adding an optional parameter, changing a type, reordering — orphans every job already in the queue.

**Why:** Production incident on PR #663: that PR added an optional `bool scheduleRetries = true` to the end of `IGoogleGroupSync.ReconcileOneAsync`. Compilation and tests passed (optional, end-of-list, default supplied). Deploy succeeded. But every `ReconcileOneAsync` job that was queued before the deploy now failed with `InvalidOperationException: The type ... does not contain a method with signature ReconcileOneAsync(String, SyncAction, CancellationToken, Int32)`, retrying 10 times before dropping. Fix lived in PR (this PR): add a 4-param overload matching the old shape and route the scheduler calls to it explicitly.

**How to apply:**

1. **Add a dedicated overload for the Hangfire boundary** — no default parameters, parameter list locked. This is the contract Hangfire serializes against.
2. **In the scheduler, pass every parameter explicitly** (no relying on defaults), so the compiler binds to the no-defaults overload and the captured `MethodInfo` is stable:
   ```csharp
   // Good — binds to the 4-param overload, signature locked
   backgroundJobs.Enqueue<IGoogleGroupSync>(
       sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None, 0));

   // Bad — binds to whatever overload happens to be applicable today;
   // adding a default param later silently re-binds it
   backgroundJobs.Enqueue<IGoogleGroupSync>(
       sync => sync.ReconcileOneAsync(groupKey, SyncAction.Execute, CancellationToken.None));
   ```
3. **Let the in-process method evolve freely** as a separate, fuller overload. Direct callers (services, tests) use it; the Hangfire shim delegates into it. New behavior knobs go on the in-process overload, never on the Hangfire-bound one.
4. **If you ever do need to change the Hangfire-bound signature** (rename, type change, removal), drain the relevant queue first — pause enqueueing, let the queue empty, then deploy.

**Related:** [`interface-method-additions-are-debt`](../architecture/interface-method-additions-are-debt.md) — the cousin rule for interfaces in general; the Hangfire boundary is stricter because the wire format is in storage, not just in source.
