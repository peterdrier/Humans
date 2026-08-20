# Scanner — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ScannerController` | Class | `TicketAdmin, Admin, Board` OR the gate-terminal shared account (by well-known id) | `PolicyNames.ScannerAccess` (composite assertion — also admits `SystemUserIds.GateTerminal` by NameIdentifier claim so the shared kiosk session can scan without holding any role; all actions inherit — `Index`, `Barcode`, `Tickets`, `Tickets/Card`) |

`ScannerController.Search` is a deliberately name-only, masked-email people search so the route-locked kiosk never exposes the broader `/api/profiles/search` surface.
