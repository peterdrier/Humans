# Ticket transfer UI tweaks + vendor step history

**Date:** 2026-05-12
**Branch:** `ticket-transfer-tweaks` (off `origin/main`)
**Scope:** Polish the ticket-transfer admin/user surfaces, add structured vendor-step history, fix the onward-transfer ownership bug, and add a manual retry path for partially-failed vendor writebacks.

## Goals

1. Stop rolling our own person-rendering inside the ticket-transfer views — route through the canonical `<vc:human>` everywhere.
2. Give Peter/admins a glanceable "what tickets does this human have" panel on the profile pages and the transfer-review page.
3. Make partial vendor failures (void-succeeded-issue-failed, etc.) recoverable without leaving the TT dashboard — capture the hold ID, surface the failure, and provide a single-click retry.
4. Fix the latent bug where a recipient of a successful transfer cannot transfer the ticket onward.

## Non-goals (called out for scope clarity)

- **Folding email-exact lookup into the canonical `/api/profiles/search` API.** The Send page's `POST /Tickets/Transfers/Lookup` stays for now; only its rendered output changes. A separate PR can unify the search path later.
- **Mitigating the pre-existing "receiver's email not verified at transfer time" risk.** Documented as a known edge case in §7; not addressed in this PR.
- **Backfilling vendor-step history for transfers that completed before this PR.** New JSON column defaults to `[]`; old rows render "no step detail recorded" on the timeline.

---

## 1. Shared ticket-holdings view component

New: `<vc:ticket-holdings user-id="…" show-empty="false" />`

**Data shape** (single new service method on `ITicketQueryService`, returning a flat record):

```csharp
public record UserTicketHoldings(
    int OrderCount,                       // orders where TicketOrder.MatchedUserId == userId
    IReadOnlyList<string> AttendeeNames); // attendee names of tickets where current_owner == userId

Task<UserTicketHoldings> GetUserTicketHoldingsAsync(Guid userId, CancellationToken ct = default);
```

Where `current_owner(attendee) = attendee.MatchedUserId ?? attendee.TicketOrder.MatchedUserId` — same helper used everywhere else in the bundle (see §6).

**Render** — two compact rows inside a small card:

```
🧾 2 orders
🎟️ 3 tickets — Peter Drier, Alice Smith, Bob Q.
```

`show-empty="false"` (default) → render nothing when both are zero. Profile self-view and "no ticket activity" admin views pass the default. Admin-detail and transfer-review surfaces pass `show-empty="true"` so an empty result is informative ("this person has no tickets") rather than missing.

**Placements:**

| Page | Surface |
|---|---|
| `/Profile` (self, `Profile/Index.cshtml`) | Top of the right-hand `col-md-4` Quick Actions column, above the action list |
| `/Profile/AdminDetail/{id}` | Right column (mirrors the existing card placement pattern there) |
| `/Tickets/Admin/Transfers/Detail/{id}` | Under the Sender card, in the same column |

**Caching:** results go through the existing `ITicketQueryService` cache contract. `InvalidateAfterTransfer` already invalidates both parties — extend it to also evict the holdings cache for both. One new cache key (`UserTicketHoldings:{userId}`).

---

## 2. Admin Transfers Index — column layout and `<vc:human>`

`/Tickets/Admin/Transfers` (`Views/TicketTransferAdmin/Index.cshtml`) goes from:

```
Requested | From (text) | To (text + email) | Ticket | Reason | (action)
```

to:

```
Requested | Requester (vc:human AvatarName Admin)
        | Ticket (OriginalAttendeeName + small TicketTypeName)
        | Recipient (vc:human AvatarName Admin)
        | Reason | Review
```

`ReceiverLegalName`/`ReceiverEmail` snapshots remain in the DTO/row entity — they're audit fixtures, just no longer drive the UI.

## 3. Admin Transfers Detail — `<vc:human>` cards + step timeline + audit embed

`/Tickets/Admin/Transfers/Detail/{id}` (`Views/TicketTransferAdmin/Detail.cshtml`) replaces the hand-rolled person cards with:

```cshtml
<div class="row g-3 mb-3">
  <div class="col-md-6">
    <h3 class="h6 text-uppercase text-muted">Sender</h3>
    <vc:human user-id="@row.SenderUserId" layout="Card" link="Admin" />
    <vc:ticket-holdings user-id="@row.SenderUserId" show-empty="true" />
  </div>
  <div class="col-md-6">
    <h3 class="h6 text-uppercase text-muted">Recipient</h3>
    <vc:human user-id="@row.ReceiverUserId" layout="Card" link="Admin" />
  </div>
</div>
```

(Holdings on the Sender only — admin context for "1 of N" framing.)

Below the decide form, two new sections:

```cshtml
@* Structured vendor-step timeline — pulled from request.VendorStepsJson *@
<vc:ticket-transfer-timeline request="@Model.Row" />

@* Existing audit-log component, scoped to this request *@
<vc:audit-log entity-type="TicketTransferRequest" entity-id="@row.Id" title="Request audit history" />
```

The timeline view component (new) renders the deserialised step list as a vertical list, with status/timestamp/reference-id/error per step.

**TicketTailor deep-link on the Ticket card.** The two lenses above are *our* view (what we asked TT to do and what TT replied). The vendor's own audit lives in the TicketTailor dashboard — not exposed via API, so we can't mirror it. We can deep-link to it. `TicketOrder.VendorDashboardUrl` is already captured at order sync; render it as a button on the Ticket card:

```cshtml
@if (!string.IsNullOrEmpty(Model.OrderDashboardUrl))
{
    <a href="@Model.OrderDashboardUrl" target="_blank" rel="noopener"
       class="btn btn-sm btn-outline-secondary">
        View order in TicketTailor <i class="fa-solid fa-arrow-up-right-from-square"></i>
    </a>
}
```

The Detail DTO grows one new field (`OrderDashboardUrl`) populated from `attendee.TicketOrder.VendorDashboardUrl` when building `TicketTransferDetailDto`.

## 4. Send page — render swap, search path unchanged

`/Tickets/Transfers/Send` (`Views/TicketTransfer/Send.cshtml`):

- Multi-match list rows → `<vc:human user-id="@r.UserId" layout="AvatarName" link="None" />` inside the existing `list-group` (still wrapped in a `<form>` per row so click = "I pick this one").
- Single-match confirm card → `<vc:human user-id="@receiver.UserId" layout="Card" link="None" />`.

The `POST /Tickets/Transfers/Lookup` endpoint and `LookupReceiversAsync` service method stay as-is. Add a code comment near `LookupReceiversAsync` explaining the divergence from `/api/profiles/search` (email-exact path not yet supported there) and pointing at `memory/architecture/person-search.md` so future readers don't re-fork on accident.

---

## 5. Vendor step log — structured history

### Schema

Add to `TicketTransferRequest`:

```csharp
/// <summary>JSON-serialised list of TicketTransferVendorStep. Empty list for
/// requests created before the step-log feature.</summary>
public string VendorStepsJson { get; set; } = "[]";
```

Migration: one new nullable-then-defaulted text column (`vendor_steps_json`), backfilled to `[]` on apply. No backfill of historical content.

New types (`Humans.Application.DTOs`):

```csharp
public enum TicketTransferVendorStepKind { Void, Issue, LocalWriteback, RetryIssue, ManualReconcile }

public record TicketTransferVendorStep(
    TicketTransferVendorStepKind Kind,
    bool Success,
    Instant OccurredAt,
    string? VendorReferenceId,      // hold id, new ticket id
    string? RequestSummary,         // short — e.g. "void vendorTicketId=tt_abc"
    string? ResponseSummary,        // short
    string? ErrorMessage);
```

### Where steps are appended

Inside `TicketTransferService.WriteToVendorAsync`:

| Sub-step | Step appended |
|---|---|
| Void call returns | `Void` step (Success/Error, VendorReferenceId = holdId on success) |
| Issue call returns | `Issue` step (Success/Error, VendorReferenceId = newVendorTicketId on success) |
| Local upsert returns | `LocalWriteback` step (Success/Error) |

The append helper takes care of: read-deserialise-list → append → re-serialise → assign back. Single-threaded per request (no concurrent step writes for the same request), so no locking required.

`AppendStep` is private to the service. The repository never sees structured `TicketTransferVendorStep` instances — only the raw JSON column.

### Reading

A small read-side helper (also private to the service) deserialises `VendorStepsJson` into `IReadOnlyList<TicketTransferVendorStep>` when building `TicketTransferDetailDto`. The DTO carries the list; the timeline view component renders it.

---

## 6. Onward-transfer fix — ownership cascade

**Bug:** `GetMyAttendeesAsync.CanSendTransfer` and `CreateRequestAsync` both check `attendee.TicketOrder.MatchedUserId == userId`. After A→B succeeds, the new TicketAttendee has `MatchedUserId=B` but the parent order's `MatchedUserId` is still A. B cannot transfer onward.

**Rule** (single static helper, namespaced `TicketAttendeeOwnership`):

```csharp
public static Guid? CurrentOwner(TicketAttendee attendee) =>
    attendee.MatchedUserId ?? attendee.TicketOrder?.MatchedUserId;

public static bool IsCurrentOwner(TicketAttendee attendee, Guid userId) =>
    CurrentOwner(attendee) == userId;
```

**Three call sites change** to use `IsCurrentOwner`:

1. `TicketTransferService.GetMyAttendeesAsync` — `CanSendTransfer` computation.
2. `TicketTransferService.CreateRequestAsync` — server-side ownership check; updated error message reads "You can only transfer tickets you currently hold."
3. `TicketQueryService.GetUserTicketHoldingsAsync` (the method backing `<vc:ticket-holdings>`) — the "tickets" projection.

**Implied behaviour notes** (documented inline at the helper):

- If both `attendee.MatchedUserId` and `TicketOrder.MatchedUserId` are null, no Humans user can transfer — vendor-only.
- If the attendee's matched email later becomes unverified and sync clears `MatchedUserId`, ownership falls back to the order buyer. Consistent fallback, not a special case.

**Tests:**
- A→B→C chain transfer succeeds, audit chain preserved per-request.
- B cannot cancel A's request; A cannot transfer B's already-matched ticket.
- Unmatched attendee → order buyer can transfer; matched attendee → only matched user can transfer.

---

## 7. Retry-issue admin action

New endpoint: `POST /Tickets/Admin/Transfers/{id}/RetryIssue`.
Authorisation: `PolicyNames.TicketAdminOrAdmin` (same as Decide).
Form: anti-forgery token + optional admin notes.

New service method on `ITicketTransferService`:

```csharp
Task<TicketTransferRowDto> RetryIssueAsync(Guid transferRequestId, Guid adminUserId,
    string? adminNotes, CancellationToken ct = default);
```

### Preconditions
- `request.Status == Approved`
- `request.VendorResult == VoidSucceededIssueFailed`
- The most recent `Void` step in `VendorStepsJson` has a non-null `VendorReferenceId` (the hold ID).

If preconditions fail → throw `InvalidOperationException` with a specific message; controller surfaces via `SetError` (existing pattern).

### Flow
1. Re-derive hold ID from the latest successful `Void` step.
2. Call `_vendor.IssueTicketAsync` against the hold ID, with the request's snapshotted Receiver name + email.
3. **On success:** insert new `TicketAttendee` row (Valid, MatchedUserId = ReceiverUserId, TicketOrderId = the original order — same shape as the happy-path), set `request.VendorResult = Succeeded`, `request.NewVendorTicketId`, append `RetryIssue` step (Success). Audit `TicketTransferApproved` with note "retry-issue success: ticket {newId}". Cache invalidate both parties.
4. **On failure:** append `RetryIssue` step (Error). Status stays `Approved` / `VoidSucceededIssueFailed`. Audit `TicketTransferApproved` with note "retry-issue failed: {error}". Toast the error to the admin.

### UI surface
Detail page, beneath the Decide form (or instead of it, when status is Approved):

```cshtml
@if (row.Status == TicketTransferStatus.Approved
     && row.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed)
{
    <form asp-action="RetryIssue" asp-route-id="@row.Id" method="post" class="mt-3">
        @Html.AntiForgeryToken()
        <p class="text-warning">
            Void succeeded but reissue failed. The hold is still reserved at the vendor.
            Retry will issue against the existing hold.
        </p>
        <button type="submit" class="btn btn-warning">Retry issue</button>
    </form>
}
```

**Not in this PR:** a "Mark vendor-side reconciled (manual)" button for the `Failed` (void rejected) path. Lower urgency since that path doesn't leave a vendor-side hold drifting. Tracked as a follow-up.

---

## 8. Index tabs + drift diagnostic

### Tabs

`/Tickets/Admin/Transfers` becomes tabbed:

| Tab | Filter | Order |
|---|---|---|
| **Pending** (default) | `Status == Pending` | `RequestedAt` asc (FIFO, unchanged) |
| **Needs attention** | `Status == Approved && VendorResult IN (Failed, VoidSucceededIssueFailed)` | `DecidedAt` desc |
| **All** | no status filter | `RequestedAt` desc |

Tabs implemented via a `tab` query-string param (no separate routes). Controller branches on the param; views share the partial.

A small badge on the **Needs attention** tab shows the count if non-zero.

### Order-drift diagnostic

New small card on the same page, above the tabs:

```
⚠ Order drift detected: 1 paid order has fewer valid tickets than originally issued.
[View]
```

The drift query (`ITicketQueryService.GetOrderDriftAsync`):

```sql
-- conceptually:
SELECT o.*
FROM ticket_orders o
WHERE o.PaymentStatus = 'Paid'
  AND (SELECT COUNT(*) FROM ticket_attendees a
       WHERE a.TicketOrderId = o.Id
         AND a.Status IN ('Valid','CheckedIn'))
    < (SELECT COUNT(*) FROM ticket_attendees a WHERE a.TicketOrderId = o.Id)
```

Returns the slim list of orders where #Valid+CheckedIn < #attendees on the order. Click-through → `/Tickets/Admin/Orders?id=…` (existing route) so admins can drill in. No drift → card hidden. The card complements the transfer-driven "Needs attention" tab — that one catches transfers we initiated; this one catches drift from any cause (manual TT-dashboard edits, refunds without resync, etc.).

---

## 9. Re-sync safety (informational — no code changes from this section)

Documented here so the bundle ships with an explicit "yes we thought about this" record. Walked through in conversation; summary:

- **Succeeded:** sync upserts both attendee rows by `VendorTicketId`. New row's `MatchedUserId` re-derives from receiver's email → still receiver. ✅
- **VoidSucceededIssueFailed:** only the voided original reconciles. Retry-issue creates the new local row and a fresh vendor record; subsequent syncs match by `VendorTicketId`. ✅
- **Failed (void rejected):** local + vendor agree on Valid; no-op. ✅
- **Partial state (vendor done, request row update failed):** attendee rows are consistent. Request row hangs Pending — fully app-managed, sync doesn't touch it. Admin reconciles the row manually. ✅
- **Known caveat (pre-existing, out of scope):** if the Receiver's email at transfer time isn't verified-on-record, the next sync's email re-derivation can clobber `attendee.MatchedUserId` to null. Ownership cascade (§6) handles this gracefully — order buyer regains transfer rights — but the receiver loses their "I have a ticket" indicator. Mitigation candidate for a follow-up: require receiver's primary email to be verified before allowing transfer.

---

## 10. File inventory

### New files
- `src/Humans.Web/ViewComponents/TicketHoldingsViewComponent.cs` + view
- `src/Humans.Web/ViewComponents/TicketTransferTimelineViewComponent.cs` + view
- `src/Humans.Application/Services/Tickets/TicketAttendeeOwnership.cs` (static helper)
- `src/Humans.Application/DTOs/TicketTransferVendorStep.cs` (record + enum)
- `src/Humans.Application/DTOs/TicketTransferDtos.cs` — extend `TicketTransferDetailDto` with `OrderDashboardUrl` (modified, not new)
- `src/Humans.Infrastructure/Migrations/<date>_AddTicketTransferVendorStepsJson.cs`
- `tests/Humans.Application.Tests/Services/Tickets/TicketAttendeeOwnershipTests.cs`
- `tests/Humans.Application.Tests/Services/Tickets/TicketTransferService_RetryIssueTests.cs`
- `tests/Humans.Application.Tests/Services/Tickets/TicketTransferService_OnwardTransferTests.cs`

### Modified files
- `src/Humans.Domain/Entities/TicketTransferRequest.cs` (+`VendorStepsJson`)
- `src/Humans.Infrastructure/Data/Configurations/Tickets/TicketTransferRequestConfiguration.cs` (column mapping)
- `src/Humans.Application/Services/Tickets/TicketTransferService.cs` (step appends, ownership cascade, RetryIssue)
- `src/Humans.Application/Interfaces/Tickets/ITicketTransferService.cs` (+`RetryIssueAsync`)
- `src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs` (+`GetUserTicketHoldingsAsync`, +`GetOrderDriftAsync`)
- `src/Humans.Application/Services/Tickets/TicketQueryService.cs` (impl + cache eviction in `InvalidateAfterTransfer`)
- `src/Humans.Web/Controllers/TicketTransferAdminController.cs` (+`RetryIssue` action, +`tab` param on Index)
- `src/Humans.Web/Controllers/TicketTransferController.cs` (no behavioural change; rendering only via view)
- `src/Humans.Web/Views/TicketTransferAdmin/Index.cshtml` (tabs, column layout, drift card)
- `src/Humans.Web/Views/TicketTransferAdmin/Detail.cshtml` (vc:human cards, timeline, audit embed, retry form)
- `src/Humans.Web/Views/TicketTransfer/Send.cshtml` (vc:human swaps)
- `src/Humans.Web/Views/Profile/Index.cshtml` (sidebar holdings card)
- `src/Humans.Web/Views/Profile/AdminDetail.cshtml` (right-column holdings card)

---

## 11. Test plan

**Unit:**
- `TicketAttendeeOwnership.IsCurrentOwner` truth table — both null, one null, both non-null matching/non-matching.
- `TicketTransferService.WriteToVendorAsync` appends Void+Issue+LocalWriteback steps in each outcome; JSON round-trips.
- `RetryIssueAsync` precondition failures (wrong status, wrong vendor result, missing hold id).
- `RetryIssueAsync` happy path → new attendee row, request flipped to Succeeded, step appended.
- `RetryIssueAsync` vendor failure → request unchanged, failed step appended, no extra attendee row.
- Onward-transfer: A→B then B→C succeeds; A cannot transfer B's matched ticket; unmatched attendee → order buyer can transfer.
- `GetUserTicketHoldingsAsync` — order count from buyer relationship; attendee names from `IsCurrentOwner`.
- `GetOrderDriftAsync` returns only paid orders where Valid+CheckedIn < total attendees.

**Integration (where existing patterns cover):**
- Migration adds the column with `[]` default; existing rows readable.
- View component renders for empty / non-empty / `show-empty` combinations.

---

## 12. Risks / open items

- **Vendor-step JSON drift.** If we evolve the `TicketTransferVendorStep` record shape later, old rows still deserialise (records with new nullable fields). System.Text.Json defaults handle this; covered by a round-trip test against a frozen historical JSON sample.
- **Tab UI clutter on a small admin queue.** If transfer volume stays low (~tens), tabs may feel heavy. Acceptable cost for the diagnostic surface; revisit if usage proves it overkill.
- **Order-drift diagnostic noise.** Refund flows that void without recreating tickets will surface here. Acceptable — admins want to know about those too; we can add a refund-aware exclusion if the noise becomes real.
