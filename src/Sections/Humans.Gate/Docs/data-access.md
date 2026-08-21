# Gate — Data Access

## Gate

Folder: `src/Sections/Humans.Gate/`, its own project. **DbContext:**
`GateDbContext`. `GateRepository` injects `IDbContextFactory<GateDbContext>`
directly. Owns `gate_scan_events`, `gate_settings`, `gate_staff_pins`. Gate
admissions: barcode scan evaluation, append-only scan/verdict recording,
personal staff claim PINs, supervisor overrides, leaderboard, and a
best-effort vendor check-in mirror back to TicketTailor.

`GateDbContext` declares real `DbSet<GateScanEvent>` /
`DbSet<GateSettings>` / `DbSet<GateStaffPin>` properties (table names come
from the `IEntityTypeConfiguration` classes under
`src/Sections/Humans.Gate/Data/Configurations/`, `ToTable("gate_…")`).
`GateRepository` accesses them via `ctx.Set<T>()` and is a **Singleton**
(`IDbContextFactory` short-lived context pattern, §15b).

**Deliberately no caching decorator** — gate reads must be live: a stale
verdict admits or blocks the wrong person. Ticket/EE inputs still come from
the cached cross-section read surfaces.

### GateService (Scoped)

Repository: `IGateRepository`.

| Table | R/W |
|-------|-----|
| gate_scan_events | R/W (append-only verdict log; admit dedupe via unique index on `AdmitDedupeKey`; retention purge; merge reassignment) |
| gate_settings | R/W (singleton row — general-entry cutoff, minor-age threshold) |
| gate_staff_pins | R/W (per-staffer PIN hash via `IPasswordHasher<GateStaffPin>`; `AdminEnrolled` flag gates override authority) |
| EventParticipations | W via `IUserService.SetParticipationFromTicketSyncAsync` (**not** a foreign table access — an admit projects an `Attended` participation row through the owning Users service, so consumed camp EE can't be revoked) |

Cross-section calls via `ITicketServiceRead` (barcode → attendee resolved by
filtering the cached orders projection in memory — no new interface method),
`IEarlyEntryService` (cached per-user EE for the too-early rule),
`IBurnSettingsService` (event timezone / active event), `IShiftManagementService`
(active event + gate-crew shift roster for the claim screen, via
`GetBrowseShiftsAsync`), `IRoleAssignmentService` (server-verified supervisor
roles for overrides), `IUserService` (participation projection),
`IAuditLogService` (PIN set/reset audit — never the PIN value), plus
`IPasswordHasher<GateStaffPin>` and `IClock` (cutoff is always evaluated
against the server clock, never a device clock). Implements `IGateService`,
`IUserMerge` (re-points `GuestUserId` / `ScannedByUserId` /
`OverrideByUserId` on merge), `IUserDataContributor` (GDPR slice
`GdprExportSections.GateScans` — data-minimized: verdict/time/role/lane,
no barcode, no other person's identifiers). No `IMemoryCache`.

### GateAdmissionRules / GateBarcode

Pure static helpers — no DI, no DB access. `GateAdmissionRules.Evaluate`
is the decision table (void / duplicate / cutoff / EE / ID-check outcomes);
`GateBarcode.Normalize` canonicalises scanned codes.

### Gate helpers holding `IMemoryCache` state

Not Application services, but they hold the section's only `IMemoryCache`
state (single-server in-memory, see Appendix B). `GatePinThrottle` and
`GateVendorMirrorLedger` live in the Gate section itself
(`src/Sections/Humans.Gate/Services/Stores/`) —
`GatePinThrottle` (`GatePinFailures:{key}` — PIN brute-force lockout, 5
failures / 15 min) and `GateVendorMirrorLedger`
(`GateVendorMirrorSent:{vendorTicketId}` — 24 h atomic claim so the vendor
check-in mirror and the backfill page never double-post a non-idempotent
TicketTailor check-in). Two helpers remain genuinely Web-layer
(`src/Humans.Web/Services/` and `Hosting/`) because they gate terminal *sign-in*,
not gate *admission*: `GateLoginThrottle` (`GateLoginFailures:{sourceIp}` —
per-IP terminal sign-in throttle) and `GateTerminalAccountSeeder`, which
provisions the shared kiosk account and fires `InvalidateUserAccess`
(claims / shift-auth / active-teams eviction).

### Background jobs (`Humans.Gate/Contracts/`)

`GateRetentionJob` (daily purge of scan rows past retention, via
`IGateService.PurgeScansBeforeAsync` — `Gate:RetentionDays`, default 365)
and `GateVendorCheckInJob` (best-effort mirror of an admit to TicketTailor
via `ITicketVendorMirror` — the Tickets Contracts leaf's narrow mirror
surface, not the full `ITicketVendorService` port; off by default behind
`Gate:VendorMirrorEnabled`, enqueued by `GateController` on admit; a
one-off backfill page covers a 30-day window).

---


