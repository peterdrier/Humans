# Gate — Data Access

## Gate

Folder: `src/Sections/Humans.Gate/`, its own project. **DbContext:**
`GateDbContext`. `GateRepository` injects `IDbContextFactory<GateDbContext>`
directly. Owns `gate_scan_events`, `gate_settings`. Gate
admissions: barcode scan evaluation, append-only scan/verdict recording,
undo of an admit, find by name, the arrivals report and supervisor overrides.

`GateDbContext` declares real `DbSet<GateScanEvent>` /
`DbSet<GateSettings>` properties (table names come
from the `IEntityTypeConfiguration` classes under
`src/Sections/Humans.Gate/Data/Configurations/`, `ToTable("gate_…")`).
`GateRepository` accesses them via `ctx.Set<T>()` and is a **Singleton**
(`IDbContextFactory` short-lived context pattern, design-rules.md, *Section caching pattern*).

**Deliberately no caching decorator** — gate reads must be live: a stale
verdict admits or blocks the wrong person. Ticket/EE inputs still come from
the cached cross-section read surfaces.

### GateService (Scoped)

Repository: `IGateRepository`.

| Table | R/W |
|-------|-----|
| gate_scan_events | R/W (append-only verdict log; admit dedupe via unique index on `AdmitDedupeKey`; retention purge; merge reassignment) |
| gate_settings | R/W (singleton row — general-entry cutoff) |
| tickets_event_participations | W via `ITicketWrite.RecordParticipationAsync` and `ITicketWrite.UndoCheckInAsync` (**not** a foreign table access — an admit projects an `Attended` participation row through the owning Tickets service, so consumed camp EE can't be revoked) |

Checkout-issued tickets come from `ITicketGateLookup` (Tickets, live, no cache): `GetIssuedTicketsForEventAsync` and `FindIssuedTicketAsync`. `GateAdmissionReads` serves
`IGateAdmissions` (admitted codes) to Tickets from `gate_scan_events`.
Cross-section calls via `ITicketService` (barcode → attendee resolved by
filtering the cached orders projection in memory — no new interface method),
`IEarlyEntryService` (cached per-user EE for the too-early rule),
`ISettingsService` (event timezone / active event), `IUserService`
(`SearchUsersAsync`, find by name), `ITicketWrite` (participation
projection and check-in undo), `IAuditLogService` (`GateAdmitUndone`), plus
`IClock` (cutoff is always evaluated against the server clock, never a device
clock). Overrides and undo verify the shared `Gate:SupervisorPin`. Implements `IGateService`,
`IUserMerge` (re-points `GuestUserId` / `ScannedByUserId` /
`OverrideByUserId` / `UndoneByUserId` on merge), `IUserDataContributor` (GDPR slice under the
export key declared as `GateService.GateScans` — data-minimized:
verdict/time/role/lane, no barcode, no other person's identifiers). No
`IMemoryCache`.

### GateAdmissionRules / GateBarcode

Pure static helpers — no DI, no DB access. `GateAdmissionRules.Evaluate`
is the decision table (void / duplicate / cutoff / EE / ID-check outcomes);
`GateBarcode.Normalize` canonicalises scanned codes.

### Gate helpers holding `IMemoryCache` state

Not Application services, but they hold the section's only `IMemoryCache`
state (single-server in-memory, see Appendix B). `GatePinThrottle` lives in the
Gate section itself (`src/Sections/Humans.Gate/Services/Stores/`,
`GatePinFailures:{key}` — supervisor PIN brute-force lockout, 5 failures /
15 min). Two helpers live outside the section because they
gate terminal *sign-in*, not gate *admission*: `GateLoginThrottle`
(`src/Humans.Web/Services/`, `GateLoginFailures:{sourceIp}` — per-IP
terminal sign-in throttle) and `GateTerminalAccountSeeder`
(`src/Sections/Humans.Tickets/Services/`), which provisions the shared
kiosk account.

### Background jobs

None. Settings' daily retention job deletes scan rows past the app-wide retention period through `GateService` as an `IRetentionPeriodContributor`.

---


