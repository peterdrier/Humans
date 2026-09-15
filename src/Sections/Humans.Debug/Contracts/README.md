# Humans.Debug — Contracts

Empty on purpose.

`Contracts/` holds everything consumed from outside the section. Debug is a pure **consumer**:
it owns no tables and no services, and nothing outside it names a Debug type. The controllers
compose over Base diagnostics singletons (`IClientStatsTracker`, `IHttpStatusTracker`,
`ConfigurationRegistry`, `QueryStatistics`, `ICacheStatsProvider`, `IEnumerable<ICacheStats>`,
`IAdminDatabaseDiagnosticsService`, `ISectionCatalog`), each registered by its own owner, so
`Section.Register` is empty too.

What Debug contributes outward goes by *name*, not by type: `SectionAdminNav` supplies the
Diagnostics and Design sidebar groups (controller/action names, all on `PolicyNames.AdminOnly`)
and `SectionChrome` supplies the `UserSetMembershipCard` view component to the admin dashboard's
chrome slot.

A folder rather than a `Humans.Debug.Contracts` project: folder vs. project is decided by
where the consumer lives, and there are no consumers at all.
