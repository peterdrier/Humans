# Team Early Entry — Ticket-Number Lookup Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a team manager type a ticket number (e.g. `4b4DGpc`) into the existing Early Entry person picker and have the personally-paired human appear as a selectable dropdown result — no new input field.

**Architecture:** The shared `<vc:human-search>` picker gains one opt-in attribute `ticket-lookup-url`. When set, its JS fires the existing `/api/profiles/search` request *and* a team-scoped ticket-lookup request in parallel, merging both into the one dropdown (same row shape, so the renderer and selection path are unchanged). The new `TeamAdminController.LookupTicket` action resolves a barcode against the current event via the already-injected-style `ITicketServiceRead.GetTicketOrdersAsync` (gate-scanner contract from #916) and returns 0-or-1 `HumanLookupSearchResult`. Resolution is controller-side filtering (Peter's hard rules), split into two pure `internal static` helpers so the bug-prone logic is unit-tested without an auth harness.

**Tech Stack:** .NET / ASP.NET Core MVC, Clean Architecture, NSubstitute + AwesomeAssertions + `[HumansFact]` xUnit, Razor view components, vanilla JS, `IStringLocalizer<SharedResource>` resx localization.

**Spec:** `docs/superpowers/specs/2026-06-09-team-early-entry-ticket-lookup-design.md`

**Build/test commands (this repo requires `-v quiet`):**
- Build: `dotnet build Humans.slnx -v quiet`
- Web tests only: `dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj -v quiet`
- Single class: append `--filter "FullyQualifiedName~TeamAdminControllerTicketLookupTests"`

---

## File Structure

- **Modify** `src/Humans.Web/Models/HumanSearchPickerViewModel.cs` — add `TicketLookupUrl` property.
- **Modify** `src/Humans.Web/ViewComponents/HumanSearchViewComponent.cs` — add `ticketLookupUrl` param, pass through.
- **Modify** `src/Humans.Web/Views/Shared/Components/HumanSearch/Default.cshtml` — parallel fetch + merge (opt-in).
- **Modify** `src/Humans.Web/Controllers/TeamAdminController.cs` — inject `ITicketServiceRead`; add `LookupTicket` action + two `internal static` helpers.
- **Modify** `src/Humans.Web/Views/TeamAdmin/EarlyEntry.cshtml` — pass `ticket-lookup-url`, update placeholder.
- **Modify** `src/Humans.Web/Resources/SharedResource.resx` (+ `.es/.ca/.de/.fr/.it`) — add `TeamAdmin_TicketLabel`.
- **Create** `tests/Humans.Web.Tests/Controllers/TeamAdminControllerTicketLookupTests.cs` — unit tests for the two helpers.

**Reuse (no new types):** `HumanLookupSearchResult` (`src/Humans.Web/Models/SearchResponseModels.cs`) is the exact `{ userId, displayName, detail, profilePictureUrl }` row shape `/api/profiles/search` returns and the picker JS already consumes. Do **not** add a record.

---

## Chunk 1: Ticket-number lookup in the Early Entry picker

### Task 1: Plumb `TicketLookupUrl` through the picker view model + view component

**Files:**
- Modify: `src/Humans.Web/Models/HumanSearchPickerViewModel.cs`
- Modify: `src/Humans.Web/ViewComponents/HumanSearchViewComponent.cs`

This is plumbing (no behaviour yet) — covered by the build, not a unit test.

- [ ] **Step 1: Add the property to the view model**

In `HumanSearchPickerViewModel.cs`, add alongside the existing properties:

```csharp
/// <summary>
/// Optional. When set, the picker also queries this URL (<c>?q=…</c>) in parallel
/// with the profile search and merges any returned rows into the same dropdown.
/// Used by the Early Entry card to resolve a human from a ticket number. Null for
/// every other caller — the second fetch is never wired up.
/// </summary>
public string? TicketLookupUrl { get; init; }
```

- [ ] **Step 2: Add the component parameter and pass it through**

In `HumanSearchViewComponent.InvokeAsync`, add a parameter (after `allowEmail`):

```csharp
        bool allowEmail = false,
        string? ticketLookupUrl = null)
```

and set it on the model:

```csharp
            AllowEmail = allowEmail,
            TicketLookupUrl = ticketLookupUrl,
        };
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds, no warnings about the new member.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Models/HumanSearchPickerViewModel.cs src/Humans.Web/ViewComponents/HumanSearchViewComponent.cs
git commit -m "feat(picker): add opt-in TicketLookupUrl to human-search component"
```

---

### Task 2: Pure helper — find the current-event attendee by barcode (TDD)

**Files:**
- Modify: `src/Humans.Web/Controllers/TeamAdminController.cs`
- Test: `tests/Humans.Web.Tests/Controllers/TeamAdminControllerTicketLookupTests.cs`

This is the bug-prone logic (current-event filter + exact `Ordinal` match), mirroring `ScannerController.Card`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Humans.Web.Tests/Controllers/TeamAdminControllerTicketLookupTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Application;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using NodaTime;

namespace Humans.Web.Tests.Controllers;

public class TeamAdminControllerTicketLookupTests
{
    private static TicketAttendeeInfo Attendee(string barcode, Guid? matchedUserId = null) =>
        new(
            Id: Guid.NewGuid(),
            VendorTicketId: "vt-" + barcode,
            AttendeeName: "Ada Lovelace",
            AttendeeEmail: "ada@example.com",
            TicketTypeName: "General Admission",
            Price: 10m,
            Status: TicketAttendeeStatus.Valid,
            MatchedUserId: matchedUserId,
            Barcode: barcode);

    private static TicketOrderInfo Order(bool isCurrentEvent, params TicketAttendeeInfo[] attendees) =>
        new(
            Id: Guid.NewGuid(),
            VendorOrderId: "ord-1",
            BuyerName: "Buyer",
            BuyerEmail: "buyer@example.com",
            TotalAmount: 10m,
            Currency: "EUR",
            DiscountCode: null,
            PaymentStatus: TicketPaymentStatus.Paid,
            VendorEventId: "evt-1",
            PurchasedAt: Instant.FromUtc(2026, 6, 1, 12, 0),
            MatchedUserId: null,
            IsCurrentEvent: isCurrentEvent,
            Attendees: attendees);

    [HumansFact]
    public void Find_CurrentEventExactBarcode_ReturnsAttendee()
    {
        var hit = Attendee("4b4DGpc");
        var orders = new[] { Order(isCurrentEvent: true, hit) };

        var result = TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "4b4DGpc");

        result.Should().BeSameAs(hit);
    }

    [HumansFact]
    public void Find_UnknownBarcode_ReturnsNull()
    {
        var orders = new[] { Order(isCurrentEvent: true, Attendee("4b4DGpc")) };

        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "nope-000")
            .Should().BeNull();
    }

    [HumansFact]
    public void Find_PastEventBarcode_ReturnsNull()
    {
        var orders = new[] { Order(isCurrentEvent: false, Attendee("4b4DGpc")) };

        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "4b4DGpc")
            .Should().BeNull();
    }

    [HumansFact]
    public void Find_DiffersByCase_ReturnsNull()
    {
        // Barcodes are case-sensitive (Ordinal) — gate-scanner contract.
        var orders = new[] { Order(isCurrentEvent: true, Attendee("4b4DGpc")) };

        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "4b4dgpc")
            .Should().BeNull();
    }

    [HumansFact]
    public void Find_EmptyOrWhitespaceQuery_ReturnsNull()
    {
        var orders = new[] { Order(isCurrentEvent: true, Attendee("4b4DGpc")) };

        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "   ").Should().BeNull();
        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, "").Should().BeNull();
        TeamAdminController.FindCurrentEventAttendeeByBarcode(orders, null).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run to verify they fail (compile error / not defined)**

Run: `dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~TeamAdminControllerTicketLookupTests" -v quiet`
Expected: FAILS to compile — `FindCurrentEventAttendeeByBarcode` does not exist.

- [ ] **Step 3: Implement the helper**

In `TeamAdminController.cs`, add `using Humans.Application;` and `using Humans.Application.Interfaces.Tickets;` to the usings, then add this `internal static` method to the class (place it near the other EarlyEntry members):

```csharp
    /// <summary>
    /// Resolve a ticket barcode to its issued attendee within the current event only
    /// (the gate-scanner admissibility scope, see <see cref="ScannerController"/> / #916).
    /// Exact, case-sensitive (<see cref="StringComparison.Ordinal"/>) — barcodes are codes,
    /// not names. Returns null for empty/whitespace input or no match.
    /// </summary>
    internal static TicketAttendeeInfo? FindCurrentEventAttendeeByBarcode(
        IReadOnlyList<TicketOrderInfo> orders, string? barcode)
    {
        var code = barcode?.Trim() ?? string.Empty;
        if (code.Length == 0)
        {
            return null;
        }

        return orders
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .FirstOrDefault(a => string.Equals(a.Barcode, code, StringComparison.Ordinal));
    }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~TeamAdminControllerTicketLookupTests" -v quiet`
Expected: 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/TeamAdminController.cs tests/Humans.Web.Tests/Controllers/TeamAdminControllerTicketLookupTests.cs
git commit -m "feat(early-entry): resolve current-event attendee by barcode (pure helper + tests)"
```

---

### Task 3: Pure helper — build the lookup row from the matched human (TDD)

**Files:**
- Modify: `src/Humans.Web/Controllers/TeamAdminController.cs`
- Test: `tests/Humans.Web.Tests/Controllers/TeamAdminControllerTicketLookupTests.cs`

Encapsulates the "only emit a row for an attendee personally paired to an active human" rule, so the silent-on-unmatched behaviour is tested without the auth harness.

- [ ] **Step 1: Add a UserInfo builder + the failing tests**

Append to `TeamAdminControllerTicketLookupTests.cs`. Add usings at the top of the file:

```csharp
using Humans.Web.Models;
using Humans.Domain.Entities;
```

Add a builder and tests inside the class:

```csharp
    private static UserInfo ActiveHuman(Guid id, string burnerName) =>
        UserInfo.Create(
            new User { Id = id, PreferredLanguage = "en" },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: new Profile
            {
                Id = Guid.NewGuid(),
                UserId = id,
                BurnerName = burnerName,
                State = ProfileState.Active,
                IsApproved = true,
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);

    // No profile => UserInfo.IsActive == false (deleted/rejected/stub).
    private static UserInfo InactiveHuman(Guid id) =>
        UserInfo.Create(
            new User { Id = id, PreferredLanguage = "en" },
            [], [], [], profile: null, [], [], [], []);

    [HumansFact]
    public void BuildRows_ActiveMatchedHuman_ReturnsOneRow()
    {
        var userId = Guid.NewGuid();
        var hit = Attendee("4b4DGpc", matchedUserId: userId);

        var rows = TeamAdminController.BuildTicketLookupRows(
            hit, ActiveHuman(userId, "Ada"), detailLabel: "Ticket #4b4DGpc");

        rows.Should().ContainSingle();
        rows[0].UserId.Should().Be(userId);
        rows[0].DisplayName.Should().Be("Ada");
        rows[0].Detail.Should().Be("Ticket #4b4DGpc");
    }

    [HumansFact]
    public void BuildRows_NullHit_ReturnsEmpty() =>
        TeamAdminController.BuildTicketLookupRows(null, null, "x").Should().BeEmpty();

    [HumansFact]
    public void BuildRows_AttendeeNotPairedToHuman_ReturnsEmpty()
    {
        var hit = Attendee("4b4DGpc", matchedUserId: null);

        TeamAdminController.BuildTicketLookupRows(hit, null, "Ticket #4b4DGpc")
            .Should().BeEmpty();
    }

    [HumansFact]
    public void BuildRows_MatchedHumanInactive_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        var hit = Attendee("4b4DGpc", matchedUserId: userId);

        TeamAdminController.BuildTicketLookupRows(hit, InactiveHuman(userId), "Ticket #4b4DGpc")
            .Should().BeEmpty();
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~TeamAdminControllerTicketLookupTests" -v quiet`
Expected: FAILS to compile — `BuildTicketLookupRows` does not exist.

- [ ] **Step 3: Implement the helper**

In `TeamAdminController.cs`, add next to the previous helper:

```csharp
    /// <summary>
    /// Build the 0-or-1 picker row for a barcode hit. A row is emitted only when the
    /// attendee is personally paired to a human (<see cref="TicketAttendeeInfo.MatchedUserId"/>)
    /// who still resolves and is active. Otherwise empty — the picker stays silent (it just
    /// shows name matches), matching the type-ahead "no result" convention.
    /// </summary>
    internal static List<HumanLookupSearchResult> BuildTicketLookupRows(
        TicketAttendeeInfo? hit, UserInfo? matchedUser, string detailLabel)
    {
        if (hit?.MatchedUserId is null || matchedUser is null || !matchedUser.IsActive)
        {
            return [];
        }

        return
        [
            new HumanLookupSearchResult(
                matchedUser.Id, matchedUser.BurnerName, detailLabel, matchedUser.ProfilePictureUrl),
        ];
    }
```

- [ ] **Step 4: Run to verify all helper tests pass**

Run: `dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~TeamAdminControllerTicketLookupTests" -v quiet`
Expected: 9 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/TeamAdminController.cs tests/Humans.Web.Tests/Controllers/TeamAdminControllerTicketLookupTests.cs
git commit -m "feat(early-entry): build ticket-lookup row only for active paired human (+ tests)"
```

---

### Task 4: Wire the `LookupTicket` action

**Files:**
- Modify: `src/Humans.Web/Controllers/TeamAdminController.cs`

Thin glue over the two tested helpers + the existing reused auth gate. (The gate `ResolveEarlyEntryManagementAsync` is shared, already-exercised code; this action is covered by the helper unit tests plus the manual check in Task 8.)

- [ ] **Step 1: Inject `ITicketServiceRead`**

Add the parameter to the primary constructor (after `localizer`):

```csharp
    IStringLocalizer<SharedResource> localizer,
    ITicketServiceRead tickets)
```

and a backing field next to the others:

```csharp
    private readonly ITicketServiceRead _tickets = tickets;
```

- [ ] **Step 2: Add the action** (place after `RemoveEarlyEntry`, before `BuildEarlyEntryPageAsync`):

```csharp
    [HttpGet("EarlyEntry/LookupTicket")]
    public async Task<IActionResult> LookupTicket(string slug, string q, CancellationToken ct)
    {
        var (teamError, _, team) = await ResolveEarlyEntryManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (!team.EarlyEntryEnabled)
        {
            return NotFound();
        }

        var orders = await _tickets.GetTicketOrdersAsync(ct);
        var hit = FindCurrentEventAttendeeByBarcode(orders, q);

        var matched = hit?.MatchedUserId is { } id
            ? await _userService.GetUserInfoAsync(id, ct)
            : null;

        var detailLabel = localizer["TeamAdmin_TicketLabel", hit?.Barcode ?? string.Empty].Value;
        return Json(BuildTicketLookupRows(hit, matched, detailLabel));
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds. (If an architecture/surface test flags the new `ITicketServiceRead` consumer, it is an allowed cross-section read-interface call — confirm no analyzer error; none expected since `ScannerController` already consumes it.)

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/TeamAdminController.cs
git commit -m "feat(early-entry): add Teams/{slug}/EarlyEntry/LookupTicket endpoint"
```

---

### Task 5: Add the localized `TeamAdmin_TicketLabel` resource

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResource.resx` (+ `.es`, `.ca`, `.de`, `.fr`, `.it`)

**Change-enforcement (CLAUDE.md):** a new resx key must be added to **all six** locale files.

- [ ] **Step 1: Add the key to each file**

In each file, add a `<data>` entry (place near the other `TeamAdmin_` entries; key + comment identical, value translated):

- `SharedResource.resx`:
  ```xml
  <data name="TeamAdmin_TicketLabel" xml:space="preserve"><value>Ticket #{0}</value><comment>{0} = ticket barcode; shown as the detail line on a ticket-resolved person-picker result</comment></data>
  ```
- `SharedResource.es.resx`: value `Entrada #{0}`
- `SharedResource.ca.resx`: value `Entrada #{0}`
- `SharedResource.de.resx`: value `Ticket #{0}`
- `SharedResource.fr.resx`: value `Billet #{0}`
- `SharedResource.it.resx`: value `Biglietto #{0}`

(Keep the same `<comment>` on each for translator context.)

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds (resx compiles).

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Resources/SharedResource*.resx
git commit -m "i18n: add TeamAdmin_TicketLabel across all six locales"
```

---

### Task 6: Picker JS — parallel fetch + merge (opt-in)

**Files:**
- Modify: `src/Humans.Web/Views/Shared/Components/HumanSearch/Default.cshtml`

When `TicketLookupUrl` is null (every other caller), behaviour is equivalent to today: a single fetch, then render.

- [ ] **Step 1: Serialize the URL to JS**

In the `@{ … }` block near the other `…Json` locals, add:

```csharp
    // Optional team-scoped ticket-number lookup. Null for all callers except the
    // Early Entry card; when set, the picker also resolves a ticket barcode → human.
    var ticketLookupUrlJson = System.Text.Json.JsonSerializer.Serialize(Model.TicketLookupUrl);
```

- [ ] **Step 2: Read it in the script**

In the IIFE, next to `var allowEmail = …;`:

```javascript
    var ticketLookupUrl = @Html.Raw(ticketLookupUrlJson);
```

- [ ] **Step 3: Replace `fetchResults` with a merge over both sources**

Replace the existing `fetchResults` function with:

```javascript
    function fetchJsonOrEmpty(url) {
        return fetch(url, { credentials: 'same-origin' })
            .then(function (r) { return r.ok ? r.json() : []; })
            .catch(function () { return []; });
    }

    function fetchResults(q) {
        var profileUrl = '/api/profiles/search?q=' + encodeURIComponent(q);
        if (scopeArg) profileUrl += '&scope=' + encodeURIComponent(scopeArg);
        if (allowEmail) profileUrl += '&allowEmail=true';

        var fetches = [fetchJsonOrEmpty(profileUrl)];
        if (ticketLookupUrl) {
            fetches.push(fetchJsonOrEmpty(ticketLookupUrl + '?q=' + encodeURIComponent(q)));
        }

        // Each source resolves to [] on error, so one failing (e.g. ticket 403) still
        // renders the other's results. Profile rows first, then any ticket-resolved row.
        Promise.all(fetches).then(function (lists) {
            var merged = [];
            lists.forEach(function (list) {
                if (Array.isArray(list)) { merged = merged.concat(list); }
            });
            renderDropdown(merged);
        });
    }
```

- [ ] **Step 4: Build + verify other pickers unaffected**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds. Manual reasoning check: with `ticketLookupUrl === null`, `fetches` has one entry and the merged list equals the single profile response — same as before.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Views/Shared/Components/HumanSearch/Default.cshtml
git commit -m "feat(picker): merge an opt-in ticket-lookup source into the dropdown"
```

---

### Task 7: Enable it on the Early Entry card

**Files:**
- Modify: `src/Humans.Web/Views/TeamAdmin/EarlyEntry.cshtml`

- [ ] **Step 1: Pass the lookup URL + update the placeholder**

Replace the existing `<vc:human-search …>` in the *Grant early entry* card with:

```cshtml
                <vc:human-search field-name="UserId"
                                 instance-key="team-ee-add"
                                 placeholder="Search by name or ticket #…"
                                 scope="Name"
                                 ticket-lookup-url="@Url.Action("LookupTicket", "TeamAdmin", new { slug = Model.Slug })" />
```

(Placeholder stays a literal to match the existing line's idiom; the localized string is the result `Detail`.)

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/TeamAdmin/EarlyEntry.cshtml
git commit -m "feat(early-entry): accept a ticket number in the Add person picker"
```

---

### Task 8: Full verification

- [ ] **Step 1: Whole solution build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds, no new warnings.

- [ ] **Step 2: Full test suite**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all green, including the 9 new `TeamAdminControllerTicketLookupTests`.

- [ ] **Step 3: Manual exercise (per CLAUDE.md — UI changes must be run)**

Run the app (`dotnet run --project src/Humans.Web`), sign in as a manager of a team with Early Entry enabled, open `Teams/{slug}/EarlyEntry`, and in the Human box:
- type a known current-event ticket number paired to an active human → that human appears as a selectable row labelled "Ticket #…"; selecting it fills the picker; set date + project → **Grant** works.
- type a name → name results still appear (unchanged).
- type a past-event / unknown / unmatched ticket number → no ticket row (silent), name search still works.

Record the result in the PR description (evidence before "done").

- [ ] **Step 4: Open the PR**

```bash
git push -u origin team-ee-ticket-lookup
gh pr create --base main --head team-ee-ticket-lookup \
  --title "feat(early-entry): resolve a human by ticket number in the Add person picker" \
  --body "Implements docs/superpowers/specs/2026-06-09-team-early-entry-ticket-lookup-design.md. Adds an opt-in ticket-number lookup to the existing Early Entry person picker; manual verification notes below."
```

(PRs go to `origin/main` per the two-remote workflow; promotion to upstream is a later `/pr-prod`.)

---

## Notes for the implementer

- **No new cross-section surface.** Do not add a method to `ITicketServiceRead` (`[SurfaceBudget(2)]`). Resolution is controller-side via the existing `GetTicketOrdersAsync`.
- **Reuse `HumanLookupSearchResult`.** Do not create a `TicketLookupRow`.
- **Barcodes are `Ordinal`.** Never lowercase/normalize the query for matching.
- **Silent on unmatched** is intentional (design decision) — do not add an error row or message.
- If `dotnet build` surfaces a ReSharper/analyzer note, see `docs/architecture/code-analysis.md`; the new `ITicketServiceRead` consumer follows the existing `ScannerController` precedent.
</content>
