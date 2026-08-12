# Humans.Debug — Contracts

Empty on purpose.

`Contracts/` holds everything consumed from outside the section (G5-SECTION-TEMPLATE.md
step 5b). Debug is a pure **consumer**: it owns no tables and no services, and nothing outside
it names a Debug type. The controller composes over Base diagnostics singletons
(`IClientStatsTracker`, `IHttpStatusTracker`, `ConfigurationRegistry`, `QueryStatistics`,
`ICacheStatsProvider`, `IEnumerable<ICacheStats>`, `IAdminDatabaseDiagnosticsService`), each
registered by its own owner, so `Section.Register` is empty too.

The one thing outside the section that reaches Debug is `AdminNavTree`, and it names the
controller by *name* rather than by type — eleven entries across the Diagnostics and
Reference groups, all on `PolicyNames.AdminOnly`.

A folder rather than a `Humans.Debug.Contracts` project: folder vs. project is decided by
where the consumer lives, and there are no consumers at all.
