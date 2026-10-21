# EarlyEntry — Data Access

## Early Entry

Folder: `src/Sections/Humans.EarlyEntry/Services/`. Owns no DB tables —
fan-out orchestrator over per-section `IEarlyEntryProvider`
implementations. No caching decorator.

### EarlyEntryService (Scoped, registered directly as `IEarlyEntryService`)

No repository. Injects `IEnumerable<IEarlyEntryProvider>` — every section
that grants early entry implements this and contributes its rows.
Current providers: Camps (`CachingCampService` — camp-lead grants), Shifts
(`VolunteerTrackingExportService` — confirmed build-shift grants), Teams
(`TeamService` — role-gated team EE grants). Each reads through its own
section; fan-out is sequential (`Docs/EarlyEntry.md` Invariants).
No direct DB access, no cache: `GetRosterAsync` and `GetForUserAsync` both
fan out on every call, so no section evicts anything here.
