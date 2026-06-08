# Scanner ticket lookup by barcode — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pull the Ticket Tailor `barcode` into our data, let a gate scanner (`/Scanner/Tickets`) and ticket admins look a ticket up by the code printed on it, and drop the confusing `ti_…` serial from the member-facing stub.

**Architecture:** Option B (no new interface surface). `barcode` lands on `TicketAttendee`; the existing cached `TicketOrderInfo`/`TicketAttendeeInfo` projection gains `Barcode` plus void→transfer detail; the Scanner controller calls the **existing** `ITicketServiceRead.GetTicketOrdersAsync()` and filters in memory. Admin search folds barcode into the existing attendees query. The decode JS is reused from `/Scanner/Barcode`.

**Tech Stack:** ASP.NET Core MVC, EF Core (PostgreSQL), NodaTime, xUnit + FluentAssertions, vanilla ES modules (`BarcodeDetector` + `@zxing/browser`).

**Spec:** `docs/superpowers/specs/2026-06-08-scanner-ticket-lookup-design.md`

**Working tree:** `H:\source\Humans\.worktrees\scanner-ticket-lookup` (branch `feat/scanner-ticket-lookup`). Build/test with `dotnet build Humans.slnx -v quiet` / `dotnet test Humans.slnx -v quiet`.

---

## Task 1: Carry `barcode` through the vendor connector → entity

**Files:**
- Modify: `src/Humans.Domain/Entities/TicketAttendee.cs`
- Modify: `src/Humans.Application/DTOs/TicketVendorDtos.cs:28-36` (`VendorTicketDto`)
- Modify: `src/Humans.Infrastructure/Services/TicketTailorService.cs` (`TtIssuedTicket` ~347-358, `GetIssuedTicketsAsync` mapping ~137-148)
- Modify: `src/Humans.Infrastructure/Services/StubTicketVendorService.cs` (two `VendorTicketDto` build sites: ~188 and ~231)
- Modify: `src/Humans.Application/Services/Tickets/TicketSyncService.cs` (attendee mapper ~276)
- Modify: `src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs` (`UpsertAttendeesAsync` update branch ~238-245)

- [ ] **Step 1: Add the entity field.** In `TicketAttendee.cs`, after `AttendeeEmail`:

```csharp
/// <summary>Short scannable code printed on the ticket / encoded in its QR
/// (Ticket Tailor <c>issued_ticket.barcode</c>, e.g. "xyz34Qy5"). Distinct from
/// VendorTicketId (the ti_… object id). Null until a sync repopulates the row.</summary>
public string? Barcode { get; set; }
```

- [ ] **Step 2: Add to the vendor DTO.** In `VendorTicketDto`, append a parameter (named-arg callers, order is not load-bearing):

```csharp
    string Status,
    Instant? CheckedInAt = null,
    string? Barcode = null);
```

- [ ] **Step 3: Map it in the real connector.** In `TicketTailorService.TtIssuedTicket`, add:

```csharp
        [property: JsonPropertyName("barcode")] string? Barcode = null);
```

Then in `GetIssuedTicketsAsync`, add `Barcode: ticket.Barcode` to the `VendorTicketDto` construction (alongside `CheckedInAt:`).

- [ ] **Step 4: Map it in the stub connector.** Add a small deterministic generator and set `Barcode:` on **both** `VendorTicketDto` build sites:

```csharp
// 8-char alnum, deterministic per vendor ticket id, mimics a Ticket Tailor barcode.
private static string MakeBarcode(string vendorTicketId)
{
    const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789abcdefghijkmnpqrstuvwxyz";
    var h = (uint)DeterministicHash(vendorTicketId);
    var chars = new char[8];
    for (var i = 0; i < 8; i++) { chars[i] = alphabet[(int)(h % (uint)alphabet.Length)]; h = (h * 31) + 7; }
    return new string(chars);
}
```

Add `Barcode: MakeBarcode(vendorTicketId)` to the main-loop ticket (~188) and the non-paid-loop ticket (~231).

- [ ] **Step 5: Map DTO → entity in sync.** In `TicketSyncService` attendee mapper (~276, the `new TicketAttendee { … }`), add:

```csharp
            Barcode = dto.Barcode,
```

- [ ] **Step 6: Preserve it on re-sync.** In `TicketRepository.UpsertAttendeesAsync`, inside the `existing.TryGetValue(...)` update branch (after `tracked.MatchedUserId = attendee.MatchedUserId;`), add:

```csharp
                tracked.Barcode = attendee.Barcode;
```

- [ ] **Step 7: Build.** Run: `dotnet build Humans.slnx -v quiet` — Expected: succeeds (no migration yet; column not mapped until Task 2).

- [ ] **Step 8: Commit.**

```bash
git add -A && git commit -m "feat(tickets): carry Ticket Tailor barcode through connector to TicketAttendee"
```

---

## Task 2: EF mapping + migration for `ticket_attendees.barcode`

**Files:**
- Modify: `src/Humans.Infrastructure/Data/Configurations/Tickets/TicketAttendeeConfiguration.cs`
- Create: one generated migration under `src/Humans.Infrastructure/Migrations/`

- [ ] **Step 1: Configure the column + index.** In `TicketAttendeeConfiguration.Configure`, after the `AttendeeEmail` property block, add:

```csharp
        builder.Property(a => a.Barcode)
            .HasMaxLength(100);

        builder.HasIndex(a => a.Barcode);
```

(Non-unique — supports the admin search and the gate lookup; nulls are allowed for not-yet-synced rows.)

- [ ] **Step 2: Generate the migration.** From the worktree root:

```bash
dotnet ef migrations add AddTicketAttendeeBarcode \
  --project src/Humans.Infrastructure --startup-project src/Humans.Web -v quiet
```

Expected: a new `*_AddTicketAttendeeBarcode.cs` adding the `Barcode` column + `IX_ticket_attendees_Barcode`, plus a `.Designer.cs` and snapshot update. **Do not hand-edit** the generated files (see `memory/process/`). If the migration looks wrong, fix the model/config and regenerate.

- [ ] **Step 3: Build + verify migration applies.** Run: `dotnet build Humans.slnx -v quiet`. Expected: succeeds. (Schema-only change; barcode backfills operationally via a Full Re-sync — no data migration.)

- [ ] **Step 4: Commit.**

```bash
git add -A && git commit -m "feat(tickets): add ticket_attendees.barcode column + index"
```

---

## Task 3: Enrich the cached read model (barcode + void→transfer detail)

**Files:**
- Modify: `src/Humans.Application/TicketOrderInfo.cs` (`TicketAttendeeInfo`)
- Modify: `src/Humans.Application/Services/Tickets/TicketQueryService.cs` (constructor deps, `GetTicketOrdersAsync` ~55-64, `Project` ~755-779)
- Test: `tests/Humans.Application.Tests/Tickets/TicketQueryServiceTests.cs` (or the existing Tickets test file for this service)

- [ ] **Step 1: Write the failing test.** Given an order with a `Void` attendee that is the `OriginalTicketAttendeeId` of an **Approved** transfer, `GetTicketOrdersAsync` returns that attendee with `TransferredToName`/`TransferredAt` populated and `Barcode` set; a `Valid` attendee with no transfer has both transfer fields null.

```csharp
[HumansFact]
public async Task GetTicketOrdersAsync_VoidAttendeeWithApprovedTransfer_CarriesRecipientAndBarcode()
{
    var attendeeId = Guid.NewGuid();
    var order = OrderWith(new TicketAttendee {
        Id = attendeeId, VendorTicketId = "ti_1", Barcode = "xyz34Qy5",
        Status = TicketAttendeeStatus.Void, AttendeeName = "Sender" });
    ticketRepo.SetOrders(order);
    transferRepo.SetApproved(new TicketTransferRequest {
        OriginalTicketAttendeeId = attendeeId, Status = TicketTransferStatus.Approved,
        ReceiverLegalName = "Real Receiver", DecidedAt = Instant.FromUtc(2026, 6, 1, 0, 0) });

    var info = (await sut.GetTicketOrdersAsync()).Single().Attendees.Single();

    info.Barcode.Should().Be("xyz34Qy5");
    info.TransferredToName.Should().Be("Real Receiver");
    info.TransferredAt.Should().Be(Instant.FromUtc(2026, 6, 1, 0, 0));
}
```

(Use the test doubles already used by the existing `TicketQueryService` tests; add a fake `ITicketTransferRepository` returning the seeded Approved list from `GetByStatusAsync`.)

- [ ] **Step 2: Run it — expect FAIL** (compile error: `TicketAttendeeInfo` has no `Barcode`/`TransferredToName`). Run: `dotnet test Humans.slnx -v quiet --filter GetTicketOrdersAsync_VoidAttendeeWithApprovedTransfer_CarriesRecipientAndBarcode`

- [ ] **Step 3: Extend the projection record.** In `TicketOrderInfo.cs`, add to `TicketAttendeeInfo`:

```csharp
    Guid? MatchedUserId,
    string? Barcode = null,
    string? TransferredToName = null,
    Instant? TransferredAt = null);
```

- [ ] **Step 4: Inject the transfer repo + build the lookup.** In `TicketQueryService` add `ITicketTransferRepository ticketTransferRepository` to the primary constructor (same Tickets section — allowed). Update `GetTicketOrdersAsync`:

```csharp
public async Task<IReadOnlyList<TicketOrderInfo>> GetTicketOrdersAsync(CancellationToken ct = default)
{
    var syncState = await ticketRepository.GetSyncStateAsync(ct);
    var currentEventId = syncState?.VendorEventId;
    var orders = await ticketRepository.GetAllOrdersWithAttendeesAsync(ct);

    var approved = await ticketTransferRepository.GetByStatusAsync(TicketTransferStatus.Approved, ct);
    var transfersByAttendee = approved
        .GroupBy(t => t.OriginalTicketAttendeeId)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.DecidedAt).First());

    return orders.Select(o => Project(o, currentEventId, transfersByAttendee)).ToList();
}
```

- [ ] **Step 5: Update `Project`.** Change the signature and the attendee projection:

```csharp
private static TicketOrderInfo Project(
    TicketOrder o, string? currentEventId,
    IReadOnlyDictionary<Guid, TicketTransferRequest> transfersByAttendee) => new(
    // …unchanged order fields…
    Attendees: o.Attendees.Select(a => new TicketAttendeeInfo(
        Id: a.Id,
        VendorTicketId: a.VendorTicketId,
        AttendeeName: a.AttendeeName,
        AttendeeEmail: a.AttendeeEmail,
        TicketTypeName: a.TicketTypeName,
        Price: a.Price,
        Status: a.Status,
        MatchedUserId: a.MatchedUserId,
        Barcode: a.Barcode,
        TransferredToName: a.Status == TicketAttendeeStatus.Void
            && transfersByAttendee.TryGetValue(a.Id, out var tr) ? tr.ReceiverLegalName : null,
        TransferredAt: a.Status == TicketAttendeeStatus.Void
            && transfersByAttendee.TryGetValue(a.Id, out var tr2) ? tr2.DecidedAt : null)).ToList(),
    StripeFee: o.StripeFee,
    ApplicationFee: o.ApplicationFee);
```

- [ ] **Step 6: Run the test — expect PASS.** Run: `dotnet test Humans.slnx -v quiet --filter GetTicketOrdersAsync_VoidAttendeeWithApprovedTransfer_CarriesRecipientAndBarcode`

> **No new cache invalidation needed.** `Project` only sets the transfer fields for `Void` attendees. An attendee becomes `Void` only via the ticket sync that reconciles the manual TT void — and that sync already calls `ITicketCacheInvalidator.InvalidateAll()`, refreshing this projection. The Approved-but-not-yet-void window renders nothing extra (attendee is still `Valid`), so the existing `InvalidateAfterTransfer` per-user eviction is sufficient.

- [ ] **Step 7: Update the architecture test if it pins deps.** Check `tests/Humans.Application.Tests/Architecture/TicketQueryArchitectureTests.cs` — if it asserts the exact constructor dependency set, add `ITicketTransferRepository` (a `[Section("Tickets")]` repo, so it satisfies "same-section repositories only"). Run: `dotnet test Humans.slnx -v quiet --filter TicketQueryArchitectureTests`. Expected: PASS.

- [ ] **Step 8: Commit.**

```bash
git add -A && git commit -m "feat(tickets): enrich TicketAttendeeInfo with barcode + void transfer detail"
```

---

## Task 4: Admin barcode search on `/Tickets/Attendees`

**Files:**
- Modify: `src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs` (`GetAttendeesPageAsync` search clause ~677-685)
- Modify: `src/Humans.Web/Views/Ticket/Attendees.cshtml` (search box placeholder/help text)
- Test: `tests/Humans.Infrastructure.Tests/...` repository test for `GetAttendeesPageAsync` (or the existing one)

- [ ] **Step 1: Write the failing test.** Seed two attendees, one with `Barcode = "xyz34Qy5"`; `GetAttendeesPageAsync(search: "xyz34Qy5", …)` returns only that one.

- [ ] **Step 2: Run it — expect FAIL** (search currently matches only name/email).

- [ ] **Step 3: Add barcode to the search predicate.** In `GetAttendeesPageAsync`, extend the `HasSearchTerm` block:

```csharp
            query = query.Where(a =>
                a.AttendeeName.ToLower().Contains(normalizedSearch) ||
                (a.AttendeeEmail != null && a.AttendeeEmail.ToLower().Contains(normalizedSearch)) ||
                (a.Barcode != null && a.Barcode.ToLower().Contains(normalizedSearch)));
```

- [ ] **Step 4: Run the test — expect PASS.**

- [ ] **Step 5: Update the search hint.** In `Attendees.cshtml`, adjust the search input placeholder to mention barcode (follow the existing localized-string pattern if that view uses `Localizer`; otherwise edit the literal placeholder).

- [ ] **Step 6: Commit.**

```bash
git add -A && git commit -m "feat(tickets): search attendees by barcode"
```

---

## Task 5: Remove the `ti_…` serial from the member stub

**Files:**
- Modify: `src/Humans.Web/Views/Shared/Components/TicketStub/Default.cshtml` (serial block ~62-68; CSS `.ticket-stub-serial` ~24 if now unused)
- Modify: `src/Humans.Application/DTOs/TicketTransferDtos.cs` (`TicketStubInfo` + `From`) — **gated, see Step 2**
- Test: `tests/Humans.Web.Tests/...` view/component test asserting no `ti_` rendered (if such a test harness exists; otherwise rely on the grep check)

- [ ] **Step 1: Remove the serial render.** In `TicketStub/Default.cshtml`, delete the `VendorTicketId` `<span class="ticket-stub-serial">` block (keep or drop the 🎟️ icon — implementer's call). Remove the now-unused `.ticket-stub-serial` CSS rule.

- [ ] **Step 2: Drop the dead field (gated).** Grep for remaining readers:

```bash
grep -rn "\.VendorTicketId" src/Humans.Web/Views src/Humans.Web/ViewComponents | grep -i stub
grep -rn "new TicketStubInfo(\|TicketStubInfo.From(" src
```

If the serial render was the only consumer of `TicketStubInfo.VendorTicketId`, remove that parameter from the `TicketStubInfo` record **and** from `TicketStubInfo.From(...)`, then fix every construction site the second grep listed (they pass it positionally/by name). If any surface still reads it, leave the field and stop here.

- [ ] **Step 3: Build.** Run: `dotnet build Humans.slnx -v quiet`. Expected: succeeds.

- [ ] **Step 4: Verify the code is gone.** Run: `grep -rn "ticket-stub-serial\|stub-serial" src/Humans.Web/Views` — Expected: no matches.

- [ ] **Step 5: Commit.**

```bash
git add -A && git commit -m "fix(tickets): drop confusing ti_ serial from the ticket stub"
```

---

## Task 6: The `/Scanner/Tickets` gate tool

**Files:**
- Modify: `src/Humans.Web/Controllers/ScannerController.cs`
- Create: `src/Humans.Web/Models/ScannerTicketCardViewModel.cs`
- Create: `src/Humans.Web/Views/Scanner/Tickets.cshtml`
- Create: `src/Humans.Web/Views/Scanner/_TicketCard.cshtml`
- Create: `src/Humans.Web/wwwroot/js/scanner/tickets.js`
- Modify: `src/Humans.Web/wwwroot/js/scanner/barcode.js` (add optional `onHit` callback + tolerate missing results list)
- Modify: `src/Humans.Web/Views/Scanner/Index.cshtml` (add the tool card)
- Add resource keys: the `.resx` backing the Scanner views (find the file containing `Scanner_Barcode_Title`)
- Test: `tests/Humans.Web.Tests/...` controller test for the Card action

- [ ] **Step 1: View model (Web layer — formatting only).** Create `ScannerTicketCardViewModel.cs`:

```csharp
using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>Render model for the /Scanner/Tickets result card. Built in the
/// controller from the cached TicketAttendeeInfo — no new Application surface.</summary>
public sealed record ScannerTicketCardViewModel(
    bool Found,
    string? ScannedBarcode,
    TicketStubInfo? Stub,
    string? TicketTypeName,
    string? TransferredToName,
    Instant? TransferredAt);
```

- [ ] **Step 2: Write the failing controller test.** `Card("xyz34Qy5")` returns a partial whose model is `Found == true` with the matching attendee mapped; an unknown code returns `Found == false`. Use a fake `ITicketServiceRead` returning a `TicketOrderInfo` whose `Attendees` include one with `Barcode = "xyz34Qy5"`.

- [ ] **Step 3: Run it — expect FAIL** (action/constructor don't exist yet).

- [ ] **Step 4: Implement the controller.** Replace `ScannerController` with a constructor-injected `ITicketServiceRead` and the two actions:

```csharp
[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]
[Route("Scanner")]
public class ScannerController(ITicketServiceRead tickets) : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("Barcode")]
    public IActionResult Barcode() => View();

    [HttpGet("Tickets")]
    public IActionResult Tickets() => View();

    [HttpGet("Tickets/Card")]
    public async Task<IActionResult> Card(string barcode, CancellationToken ct)
    {
        var code = barcode?.Trim() ?? string.Empty;
        if (code.Length == 0)
            return PartialView("_TicketCard", new ScannerTicketCardViewModel(false, null, null, null, null, null));

        var orders = await tickets.GetTicketOrdersAsync(ct);
        var hit = orders
            .SelectMany(o => o.Attendees)
            .FirstOrDefault(a => string.Equals(a.Barcode, code, StringComparison.Ordinal));

        if (hit is null)
            return PartialView("_TicketCard", new ScannerTicketCardViewModel(false, code, null, null, null, null));

        var stub = new TicketStubInfo(
            AttendeeName: hit.AttendeeName ?? "",
            AttendeeEmail: hit.AttendeeEmail,
            Status: hit.Status,
            HasPendingTransfer: false,
            PendingTransferRequestId: null,
            EarlyEntryDate: null);
        // NOTE: this assumes Task 5 removed TicketStubInfo.VendorTicketId. If Task 5's
        // gated removal left the field in place, also pass `VendorTicketId: hit.VendorTicketId,`.

        return PartialView("_TicketCard", new ScannerTicketCardViewModel(
            true, code, stub, hit.TicketTypeName, hit.TransferredToName, hit.TransferredAt));
    }
}
```

(Add `using Humans.Application.Interfaces.Tickets;`, `Humans.Application.DTOs;`, `Humans.Web.Models;`.)

- [ ] **Step 5: Run the controller test — expect PASS.**

- [ ] **Step 6: The card partial.** Create `_TicketCard.cshtml` — reuse `<vc:ticket-stub>` and add the gate detail block:

```cshtml
@model Humans.Web.Models.ScannerTicketCardViewModel
@using Humans.Domain.Enums
@if (!Model.Found)
{
    <div class="alert alert-secondary">@Localizer["Scanner_Tickets_NotFound"] <code>@Model.ScannedBarcode</code></div>
}
else
{
    <vc:ticket-stub stub="Model.Stub" />
    <dl class="row small mt-3 mb-0">
        <dt class="col-5">@Localizer["Scanner_Tickets_Type"]</dt><dd class="col-7">@Model.TicketTypeName</dd>
        <dt class="col-5">@Localizer["Scanner_Tickets_Status"]</dt><dd class="col-7">@Model.Stub!.Status</dd>
        @if (Model.Stub.Status == TicketAttendeeStatus.Void && Model.TransferredToName is { } name)
        {
            <dt class="col-5">@Localizer["Scanner_Tickets_TransferredTo"]</dt><dd class="col-7">@name</dd>
            @if (Model.TransferredAt is { } at)
            {
                <dt class="col-5">@Localizer["Scanner_Tickets_TransferredOn"]</dt>
                <dd class="col-7">@at.ToDateTimeUtc().ToString("yyyy-MM-dd")</dd>
            }
        }
    </dl>
}
```

(Confirm the `<vc:ticket-stub>` tag-helper attribute name — match how `TicketTransfer/Index.cshtml` invokes it.)

- [ ] **Step 7: Make `initBarcodeScanner` reusable.** In `barcode.js`, accept an optional `onHit` and tolerate no results list. In `initBarcodeScanner`, destructure `onHit` from `refs`; in `addResult`, after the dedupe check and `recentHits.set(...)`, add `if (onHit) onHit(value, format);` and guard the list rendering with `if (!results) return;` immediately before the `const item = document.createElement('li')` line. (The `resultsEmpty` access is already null-guarded.)

- [ ] **Step 8: The tickets page + JS.** Create `Tickets.cshtml` modeled on `Barcode.cshtml` (breadcrumb, Start/Stop buttons, `#scanner-video`, an empty `#scanner-card` result container instead of the results list, the not-for-check-in warning stays — this is read-only). Create `tickets.js`:

```javascript
import { initBarcodeScanner } from './barcode.js';

export function initTicketScanner(refs) {
    const { card, cardUrl, ...scannerRefs } = refs;
    initBarcodeScanner({
        ...scannerRefs,
        results: null,
        resultsEmpty: null,
        onHit: async (value) => {
            try {
                const resp = await fetch(`${cardUrl}?barcode=${encodeURIComponent(value)}`,
                    { headers: { 'X-Requested-With': 'fetch' } });
                if (resp.ok) card.innerHTML = await resp.text();
            } catch (err) {
                console.error('Scanner: ticket lookup failed', err);
            }
        },
    });
}
```

The `Tickets.cshtml` `@section Scripts` imports `initTicketScanner` and passes `card: document.getElementById('scanner-card')` and `cardUrl: '@Url.Action("Card", "Scanner")'` plus the same button/video/status/error refs and `labels` as `Barcode.cshtml`.

- [ ] **Step 9: Add the tool to the Scanner index.** In `Index.cshtml`, add a second `col-md-6` card linking to `asp-action="Tickets"` (icon `fa-ticket`, blurb from a new `Scanner_Tickets_CardBlurb` key).

- [ ] **Step 10: Resource strings.** In the `.resx` that holds `Scanner_Barcode_Title`, add: `Scanner_Tickets_Title`, `Scanner_Tickets_Intro`, `Scanner_Tickets_CardBlurb`, `Scanner_Tickets_Open`, `Scanner_Tickets_NotFound`, `Scanner_Tickets_Type`, `Scanner_Tickets_Status`, `Scanner_Tickets_TransferredTo`, `Scanner_Tickets_TransferredOn`. Mirror into the localized `.resx` variants the project keeps for the other Scanner keys.

- [ ] **Step 11: Build + manual smoke.** Run: `dotnet build Humans.slnx -v quiet`. Then `dotnet run --project src/Humans.Web`, sign in as a ticket admin, open `/Scanner/Tickets`, and (dev uses the stub vendor) search a known stub barcode via `/Tickets/Attendees` to grab a value, paste it into `/Scanner/Tickets/Card?barcode=…` to confirm the card renders for valid / checked-in / void.

- [ ] **Step 12: Commit.**

```bash
git add -A && git commit -m "feat(scanner): /Scanner/Tickets gate lookup card by barcode"
```

---

## Task 7: Docs + remove the client-only architecture test

**Files:**
- Delete the test method: `tests/Humans.Application.Tests/Authorization/EndpointAuthorizationTests.cs:266-286` (`ScannerController_Remains_ClientOnly_GetSurface`)
- Modify: `docs/sections/Scanner.md`
- Modify: `docs/sections/Tickets.md`

- [ ] **Step 1: Delete the test.** Remove the entire `ScannerController_Remains_ClientOnly_GetSurface` method (the `[HumansFact]` + body). Leave the rest of `EndpointAuthorizationTests` untouched. Remove any imports left unused *by this deletion only*.

- [ ] **Step 2: Update `Scanner.md`.** Change Concepts/Invariants/Architecture: `/Scanner/Tickets` is a **server-backed** read tool that calls `ITicketServiceRead.GetTicketOrdersAsync` (cross-section read into Tickets) and renders a ticket card; `/Scanner/Barcode` stays client-only. Add both new routes to the routing table. Keep the **"not a check-in tool / no attendance mutation"** invariant — the card is read-only. Remove the line claiming the section owns no server calls and the reference to the deleted architecture test.

- [ ] **Step 3: Update `Tickets.md`.** Note `TicketAttendee.Barcode` (synced from `issued_ticket.barcode`, lazy-filled — Full Re-sync to backfill), the `TicketAttendeeInfo` enrichment (`Barcode` + void `TransferredToName`/`TransferredAt`, sourced from Approved `ticket_transfer_requests` via `ITicketTransferRepository` in `TicketQueryService`), and barcode as a searchable field on `/Tickets/Attendees`.

- [ ] **Step 4: Full build + test.** Run: `dotnet build Humans.slnx -v quiet` then `dotnet test Humans.slnx -v quiet`. Expected: all green.

- [ ] **Step 5: Commit + push.**

```bash
git add -A && git commit -m "docs(scanner,tickets): server-backed scanner lookup; drop client-only arch test"
git push
```

---

## Final verification (before opening the PR)

- [ ] `dotnet build Humans.slnx -v quiet` and `dotnet test Humans.slnx -v quiet` both clean.
- [ ] `grep -rn "ti_\|stub-serial" src/Humans.Web/Views/Shared/Components/TicketStub` — no member-facing serial.
- [ ] `/Scanner/Tickets` renders cards for valid / checked-in / void (+ transfer line) / not-found.
- [ ] `/Tickets/Attendees?search=<barcode>` returns the matching ticket.
- [ ] PR description notes the operational **Full Re-sync** needed to backfill barcodes on existing rows.

## Spec-coverage check

| Spec item | Task |
|-----------|------|
| Pull `barcode` into `TicketAttendee` | 1, 2 |
| Enrich read model; no new interface method | 3 |
| Void→transfer detail | 3 |
| Admin barcode search | 4 |
| Remove `ti_…` from stub | 5 |
| `/Scanner/Tickets` card (valid/checked-in/void/not-found) | 6 |
| Reuse `<vc:ticket-stub>` + decode JS | 6 |
| Delete client-only arch test | 7 |
| Section docs | 7 |
