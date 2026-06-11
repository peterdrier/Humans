# Team Early Entry — resolve a human by ticket number in the existing person picker

**Date:** 2026-06-09
**Status:** Design approved (pending spec review)
**Section:** Teams (page) + Tickets (read-only resolution) · **Load-bearing:** the picker
extension is reused by ~8 callers, so the opt-in default must not change their behaviour.

## Problem

On the per-team Early Entry page (`Teams/{slug}/EarlyEntry`), a coordinator grants early
entry by finding a human in the `<vc:human-search>` picker and submitting a date + project.
Sometimes the coordinator does not have a way to find the person by name (no searchable
profile, or they just don't know the burner name) but **does** have the person's ticket
reference (e.g. `4b4DGpc`). Every ticket is personally paired to a human, so the ticket
number is enough to identify them.

We want the coordinator to type that ticket number into the **same** search box and have the
paired human appear as a selectable result — no new input field.

## Existing behaviour this builds on (reused, not rebuilt)

- **The page & Add flow.** `TeamAdminController.EarlyEntry` (`Teams/{slug}`, `GET EarlyEntry`)
  renders a *Grant early entry* card. The card's `<vc:human-search field-name="UserId"
  instance-key="team-ee-add" scope="Name">` picker type-aheads against
  `/api/profiles/search` and posts a `UserId` to `EarlyEntry/Add`. This whole path is
  **unchanged** by this work.
- **Barcode data.** `TicketAttendeeInfo` (inside `TicketOrderInfo`) already carries `Barcode`
  (the Ticket Tailor `issued_ticket.barcode`) and `MatchedUserId` (the personally-paired
  human), both shipped in PR #916.
- **The resolution pattern.** `ScannerController.Card` (#916) resolves a barcode by calling
  `ITicketServiceRead.GetTicketOrdersAsync`, filtering `IsCurrentEvent`, then
  `FirstOrDefault(a => string.Equals(a.Barcode, code, StringComparison.Ordinal))`. We apply
  the identical contract.
- **Attendee→human auto-matching by email** is the Tickets-section behaviour that populates
  `MatchedUserId`. We only read it.

## Design

### 1. Shared picker: one opt-in parameter

`HumanSearchViewComponent.InvokeAsync` gains an optional `string? ticketLookupUrl = null`
parameter (tag-helper attribute `ticket-lookup-url`), carried on
`HumanSearchPickerViewModel.TicketLookupUrl`.

`Views/Shared/Components/HumanSearch/Default.cshtml`:
- Serialise the URL to JS as `ticketLookupUrl` (JSON-encoded; `null` when not supplied).
- When `ticketLookupUrl` is set, each query fires **two** fetches in parallel — the existing
  `/api/profiles/search` request and a `GET <ticketLookupUrl>?q=<query>` request — and
  concatenates the result arrays before `renderDropdown`. The ticket fetch returns the **same
  row shape** as profile search (`{ userId, displayName, detail, profilePictureUrl }`), so the
  existing renderer and the existing `mousedown`→`hiddenInput.value = r.userId` selection path
  handle a ticket-resolved row with no change.
- **Partial-failure:** the two fetches are independent — if one rejects (e.g. the ticket
  endpoint 403s) the other's results still render. Each fetch resolves to `[]` on error rather
  than failing the merge; only if *both* yield nothing does the dropdown hide.
- When `ticketLookupUrl` is null (every other caller), behaviour is byte-for-byte identical to
  today — the second fetch is never wired up.

**No change to `/api/profiles/search`** (Users-section endpoint shared by ~8 pickers — adding
Tickets there would be a cross-section leak and change behaviour everywhere). **No change to
`renderDropdown`** (unmatched tickets are silent — see §3 — so no disabled-row support needed).

Ordering: ticket rows are concatenated after profile rows. A query is almost always either a
name (0 ticket rows) or a full barcode (0 profile rows), so interleaving is a non-issue;
deduping is unnecessary because a name match and an exact-barcode match for the same person is
not a realistic collision, and even if it occurred two rows pointing at the same `userId` are
harmless.

### 2. New endpoint: `GET Teams/{slug}/EarlyEntry/LookupTicket`

On `TeamAdminController` (already the home of the EarlyEntry actions):

```
[HttpGet("EarlyEntry/LookupTicket")]
public async Task<IActionResult> LookupTicket(string slug, string q, CancellationToken ct)
```

- **Auth:** reuse `ResolveEarlyEntryManagementAsync(slug)` — identical gate to Add/Edit/Remove
  (`TeamOperationRequirement.ManageEarlyEntry` on this team). On error, return that result.
  This means only a manager of *this* team can resolve a barcode → person; the endpoint adds no
  enumeration surface beyond what the page already grants.
- **Resolution:** inject `ITicketServiceRead`; `var orders = await tickets.GetTicketOrdersAsync(ct);`
  then
  ```
  var code = q?.Trim() ?? "";
  var hit = code.Length == 0 ? null : orders
      .Where(o => o.IsCurrentEvent)
      .SelectMany(o => o.Attendees)
      .FirstOrDefault(a => string.Equals(a.Barcode, code, StringComparison.Ordinal));
  ```
- **Human projection:** if `hit?.MatchedUserId` is a value, resolve it via
  `IUserServiceRead.GetUserInfoAsync(id, ct)` (already injected as `_userService`; thread the
  action's `ct`). If the user resolves and `IsActive == true` (the same gate
  `HumanSearchViewComponent` prefill uses), return a one-row array; otherwise empty.
- **Response type: reuse the existing `HumanLookupSearchResult`** — do **not** add a new record.
  `src/Humans.Web/Models/SearchResponseModels.cs` defines
  `HumanLookupSearchResult(Guid UserId, string DisplayName, string? Detail = null, string? ProfilePictureUrl = null)`,
  which serializes to exactly `{ userId, displayName, detail, profilePictureUrl }` — the same
  shape `/api/profiles/search` (`ProfileApiController.Search`) returns and the same the picker JS
  already consumes. The action returns `Json(rows)` where `rows` is a 0-or-1 element
  `List<HumanLookupSearchResult>`.
  ```json
  [ { "userId": "<guid>", "displayName": "<BurnerName>",
      "detail": "Ticket #4b4DGpc", "profilePictureUrl": "<url-or-null>" } ]
  ```
  `Detail` is the localized "Ticket #{barcode}" label so the coordinator sees the match came from
  a ticket: `localizer["TeamAdmin_TicketLabel", hit.Barcode]` using the already-injected
  `IStringLocalizer<SharedResource> localizer`, with key `TeamAdmin_TicketLabel = "Ticket #{0}"`.
- **No new cross-section interface method** — `ITicketServiceRead` keeps `[SurfaceBudget(2)]`;
  resolution is controller-side filtering, which Peter's hard rules assign to controllers.
  `TeamAdminController` does not yet inject `ITicketServiceRead`; add that injection.

### 3. Outcomes

| Typed query resolves to… | Endpoint returns | Dropdown |
|---|---|---|
| empty / whitespace | `[]` | name results only |
| no current-event attendee with that exact barcode | `[]` | name results only (silent) |
| attendee found, `MatchedUserId == null` | `[]` | name results only (silent) |
| attendee found, matched user inactive/deleted | `[]` | name results only (silent) |
| attendee found, matched user active | one row | selectable row, `detail = "Ticket #<barcode>"` |

Unmatched is **silent** (treated like any no-result query) — chosen over a non-selectable info
row to keep the shared renderer untouched and the type-ahead conventional.

### 4. Early Entry card wiring

`Views/TeamAdmin/EarlyEntry.cshtml`, the *Grant early entry* card's picker gains one attribute:

```cshtml
<vc:human-search field-name="UserId"
                 instance-key="team-ee-add"
                 placeholder="Search by name or ticket #…"
                 scope="Name"
                 ticket-lookup-url="@Url.Action("LookupTicket", "TeamAdmin", new { slug = Model.Slug })" />
```

Only the picker attribute and placeholder change; the form, the Add action, and the name path
are otherwise untouched. (Placeholder is a localized resource string.)

## Testing

`TeamAdminController` lookup tests (Web.Tests/Controllers):

- matched barcode, active user → one row with the expected `userId` + `displayName`.
- barcode matches an attendee with `MatchedUserId == null` → `[]`.
- barcode matches an attendee whose matched user is inactive/deleted → `[]`.
- unknown barcode → `[]`.
- barcode belongs to a **non-current-event** order (`IsCurrentEvent == false`) → `[]`.
- empty / whitespace `q` → `[]` (no vendor call needed beyond the cached projection).
- non-manager caller → the `ResolveEarlyEntryManagementAsync` error result (no resolution).

The picker JS change is covered behaviourally by the existing pickers (default-null path
unchanged) plus a manual check of the merged dropdown on the Early Entry page.

## Out of scope (YAGNI)

- Creating/importing a human from an unmatched ticket (the `AttendeeContactImportService` path).
- Partial-barcode / fuzzy matching — exact `Ordinal` only.
- Multi-event scope — current event only, matching the gate scanner.
- Any change to the gate scanner, `ITicketServiceRead`, or `/api/profiles/search`.

## Files touched

- `src/Humans.Web/ViewComponents/HumanSearchViewComponent.cs` — add `ticketLookupUrl` param.
- `src/Humans.Web/Models/HumanSearchPickerViewModel.cs` — add `TicketLookupUrl`.
- `src/Humans.Web/Views/Shared/Components/HumanSearch/Default.cshtml` — parallel fetch + merge.
- `src/Humans.Web/Controllers/TeamAdminController.cs` — add `ITicketServiceRead` injection +
  `LookupTicket` action returning `List<HumanLookupSearchResult>` (existing type, no new record).
- `src/Humans.Web/Views/TeamAdmin/EarlyEntry.cshtml` — pass `ticket-lookup-url` + placeholder.
- `src/Humans.Web/Resources/SharedResource.*.resx` — "Ticket #{0}" + placeholder strings.
- `tests/Humans.Web.Tests/Controllers/` — `LookupTicket` tests.
</content>
</invoke>
