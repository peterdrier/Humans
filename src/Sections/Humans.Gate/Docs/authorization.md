# Gate — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GateController` | Class | `TicketsAdmin, Admin, Board` OR the gate-terminal shared account (by well-known id) | `PolicyNames.ScannerAccess` (gate admissions terminal at `/Gate` — distinct from the read-only Scanner section: this one decides entry and writes the durable `gate_scan_events` record; the read actions `Index`, `Evaluate`, `Find` inherit the class-level policy) |
| `GateController.Decision` | Action | `TicketsAdmin, Admin` OR the gate-terminal shared account | `PolicyNames.GateAdmit` (POST — records the admission decision, attributed to the authenticated principal; supervisor overrides — too-early / unconfirmed-EE admit, child-without-ID waiver — additionally require the shared override PIN) |
| `GateController.Undo` (POST) | Action | `TicketsAdmin, Admin` OR the gate-terminal shared account | `PolicyNames.GateAdmit` (reverses a live admit by barcode at the gate; requires the shared `Gate:SupervisorPin`, attributed to the authenticated principal) |
| `GateController.Admin` (POST) | Action | `TicketsAdmin, Admin` | `TicketsPolicies.Manage` (save target of the `/Settings#gate` tab, which carries the same policy) |
| `GateReportController` | Class | `TicketsAdmin, Admin, Board` | `TicketsPolicies.Read` (arrivals report at `/Gate/Report`; `Index` inherits) |
| `GateReportController.Undo` (POST) | Action | `TicketsAdmin, Admin` | `TicketsPolicies.Manage` (reverses a recorded admit by scan id; a reason is required; the view shows the control only to `Manage`) |

`GateAdmit` is `ScannerAccess` minus Board: the durable gate write surface never rides on the read-only Scanner gate, and Board reads the gate without admitting anyone. Supervisor overrides inside `Decision` are a second factor on top of the policy — the shared `Gate:SupervisorPin` config value, verified inside `GateService.RecordDecisionAsync` with a fixed-time hash compare and brute-force-throttled via `GatePinThrottle` (fail-closed when unconfigured) — not an identity check. `GateController.Undo` verifies the same shared PIN through the same throttle.
