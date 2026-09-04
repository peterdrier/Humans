# Rideshare — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `RideshareController` | Class | Any active human | `PolicyNames.AppAccess` (board, offer/request create-edit-cancel, interest lifecycle, `Mine`) |
| `RideshareApiController` | Class | Any active human | `PolicyNames.AppAccess` (`/api/rideshare/board` GeoJSON feed) |
| `RideshareAdminController` | Class | Admin | `PolicyNames.AdminOnly` (settings + season stats at `/Rideshare/Admin`, day roster at `/Rideshare/Admin/Day`) |

`RideshareController`'s offer/request edit and cancel actions additionally gate on ownership in
the service layer (`actor == trip.UserId` / `actor == request.UserId`) — `AppAccess` alone does
not let one human edit another's posting. Interest `Accept`/`Decline` require the actor be the
posting owner (`request.UserId` when the interest answered a pin, else `trip.UserId`); `Withdraw`
requires the actor be the interest's author or that same posting owner. There is no resource-based
`AuthorizationHandler` — ownership checks live in `IRideshareService` and surface as
`UnauthorizedAccessException` → 403.

## Negative cases

- Anonymous requests to any `/Rideshare*` or `/api/rideshare*` route are refused — no public or
  unauthenticated access to the board.
- A non-owner **cannot** edit or cancel another human's offer or request, even with `AppAccess`.
- A non-owner **cannot** accept, decline, or withdraw an interest on a posting they do not own.
- A non-Admin **cannot** reach `/Rideshare/Admin` or `/Rideshare/Admin/Day` — settings, season
  statistics, and the accepted-rider roster are Admin-only; they are never exposed on the
  member-facing board.
