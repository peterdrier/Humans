using Humans.Base.Interfaces.Admin;
using Humans.Web.Data;
using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Web.Repositories.Admin;

internal sealed class AdminDatabaseDiagnosticsRepository(
    IDbContextFactory<SystemDbContext> factory,
    IServiceScopeFactory scopeFactory,
    IEnumerable<SectionDbContextRegistration> sectionContexts)
    : IAdminDatabaseDiagnosticsRepository
{
    public async Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var pending = await db.Database.GetPendingMigrationsAsync(ct);

        // Section contexts are scoped, so resolve them through a scope, as DatabaseMigrationHostedService does.
        // The top-level fields report SystemDbContext, which this repository already opens for the Hangfire lock.
        var sections = new List<SectionMigrationStatus>();
        using var scope = scopeFactory.CreateScope();
        foreach (var section in sectionContexts)
        {
            var sectionDb = (DbContext)scope.ServiceProvider.GetRequiredService(section.ContextType);
            var sectionApplied = (await sectionDb.Database.GetAppliedMigrationsAsync(ct)).ToList();
            var sectionPending = await sectionDb.Database.GetPendingMigrationsAsync(ct);
            sections.Add(new SectionMigrationStatus(
                Context: section.ContextType.Name,
                LastApplied: sectionApplied.LastOrDefault(),
                AppliedCount: sectionApplied.Count,
                PendingCount: sectionPending.Count()));
        }

        return new DatabaseMigrationStatus(
            LastApplied: applied.LastOrDefault(),
            AppliedCount: applied.Count,
            PendingCount: pending.Count(),
            Applied: applied,
            Sections: sections);
    }

    public async Task<int> ClearHangfireLocksAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlRawAsync("DELETE FROM hangfire.lock", ct);
    }

    public async Task<DatabaseInfo> GetDatabaseInfoAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var summary = (await db.Database.SqlQueryRaw<SummaryRow>(SummarySql).ToListAsync(ct)).Single();
        var tables = await db.Database.SqlQueryRaw<TableRow>(TablesSql).ToListAsync(ct);
        var indexes = await db.Database.SqlQueryRaw<IndexRow>(IndexesSql).ToListAsync(ct);
        var connections = await db.Database.SqlQueryRaw<ConnectionRow>(ConnectionsSql).ToListAsync(ct);
        var settings = await db.Database.SqlQueryRaw<SettingRow>(SettingsSql).ToListAsync(ct);
        var extensions = await db.Database.SqlQueryRaw<ExtensionRow>(ExtensionsSql).ToListAsync(ct);

        return new DatabaseInfo(
            new DatabaseServerSummary(
                summary.ServerVersion, summary.FullVersion, summary.DatabaseName, summary.DatabaseBytes,
                summary.DatabaseSize, summary.ServerStartedAt, summary.StatsResetAt, summary.Connections,
                summary.MaxConnections, summary.CacheHitPercent, summary.Commits, summary.Rollbacks,
                summary.Deadlocks, summary.TempFiles, summary.TempBytes),
            [.. tables.Select(t => new DatabaseTableInfo(
                t.Schema, t.Name, t.Rows, t.TotalBytes, t.TableBytes, t.IndexBytes, t.DeadRows, t.SeqScans,
                t.IndexScans, t.Inserts, t.Updates, t.Deletes, t.LastVacuum, t.LastAnalyze))],
            [.. indexes.Select(i => new DatabaseIndexInfo(
                i.Schema, i.Table, i.Name, i.Bytes, i.Scans, i.IsUnique, i.IsPrimary))],
            [.. connections.Select(c => new DatabaseConnectionInfo(
                c.Pid, c.Application, c.User, c.ClientAddress, c.State, c.WaitEvent, c.BackendStart,
                c.QueryStart, c.Query))],
            [.. settings.Select(s => new DatabaseSettingInfo(s.Name, s.Value, s.Source, s.Category))],
            [.. extensions.Select(e => new DatabaseExtensionInfo(e.Name, e.Version, e.Schema))]);
    }

    private const string SummarySql = """
        SELECT current_setting('server_version') AS "ServerVersion",
               version() AS "FullVersion",
               current_database()::text AS "DatabaseName",
               pg_database_size(current_database()) AS "DatabaseBytes",
               pg_size_pretty(pg_database_size(current_database())) AS "DatabaseSize",
               pg_postmaster_start_time() AS "ServerStartedAt",
               d.stats_reset AS "StatsResetAt",
               (SELECT count(*)::int FROM pg_stat_activity a WHERE a.datname = current_database()) AS "Connections",
               current_setting('max_connections')::int AS "MaxConnections",
               round(100.0 * d.blks_hit / nullif(d.blks_hit + d.blks_read, 0), 2)::float8 AS "CacheHitPercent",
               d.xact_commit AS "Commits",
               d.xact_rollback AS "Rollbacks",
               d.deadlocks AS "Deadlocks",
               d.temp_files AS "TempFiles",
               d.temp_bytes AS "TempBytes"
        FROM pg_stat_database d
        WHERE d.datname = current_database()
        """;

    // query_to_xml runs one exact count(*) per table inside a single round trip.
    private const string TablesSql = """
        SELECT n.nspname::text AS "Schema",
               c.relname::text AS "Name",
               (xpath('/row/n/text()', query_to_xml(
                   format('SELECT count(*) AS n FROM %I.%I', n.nspname, c.relname), false, true, '')))[1]::text::bigint AS "Rows",
               pg_total_relation_size(c.oid) AS "TotalBytes",
               pg_relation_size(c.oid) AS "TableBytes",
               pg_indexes_size(c.oid) AS "IndexBytes",
               coalesce(s.n_dead_tup, 0) AS "DeadRows",
               coalesce(s.seq_scan, 0) AS "SeqScans",
               coalesce(s.idx_scan, 0) AS "IndexScans",
               coalesce(s.n_tup_ins, 0) AS "Inserts",
               coalesce(s.n_tup_upd, 0) AS "Updates",
               coalesce(s.n_tup_del, 0) AS "Deletes",
               greatest(s.last_vacuum, s.last_autovacuum) AS "LastVacuum",
               greatest(s.last_analyze, s.last_autoanalyze) AS "LastAnalyze"
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
        WHERE c.relkind = 'r'
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname NOT LIKE 'pg_toast%'
        ORDER BY pg_total_relation_size(c.oid) DESC
        """;

    private const string IndexesSql = """
        SELECT s.schemaname::text AS "Schema",
               s.relname::text AS "Table",
               s.indexrelname::text AS "Name",
               pg_relation_size(s.indexrelid) AS "Bytes",
               s.idx_scan AS "Scans",
               i.indisunique AS "IsUnique",
               i.indisprimary AS "IsPrimary"
        FROM pg_stat_user_indexes s
        JOIN pg_index i ON i.indexrelid = s.indexrelid
        ORDER BY pg_relation_size(s.indexrelid) DESC
        """;

    private const string ConnectionsSql = """
        SELECT pid AS "Pid",
               nullif(application_name, '') AS "Application",
               usename::text AS "User",
               client_addr::text AS "ClientAddress",
               state AS "State",
               CASE WHEN wait_event IS NULL THEN NULL ELSE wait_event_type || ': ' || wait_event END AS "WaitEvent",
               backend_start AS "BackendStart",
               query_start AS "QueryStart",
               left(query, 500) AS "Query"
        FROM pg_stat_activity
        WHERE datname = current_database()
        ORDER BY backend_start
        """;

    // Everything changed from its built-in default, plus the handful worth seeing even when untouched.
    private const string SettingsSql = """
        SELECT name AS "Name",
               current_setting(name) AS "Value",
               source AS "Source",
               category AS "Category"
        FROM pg_settings
        WHERE source NOT IN ('default', 'override')
           OR name IN ('shared_buffers', 'work_mem', 'maintenance_work_mem', 'effective_cache_size',
                       'max_connections', 'TimeZone', 'server_encoding', 'lc_collate', 'autovacuum')
        ORDER BY category, name
        """;

    private const string ExtensionsSql = """
        SELECT e.extname::text AS "Name",
               e.extversion AS "Version",
               n.nspname::text AS "Schema"
        FROM pg_extension e
        JOIN pg_namespace n ON n.oid = e.extnamespace
        ORDER BY e.extname
        """;

    private sealed class SummaryRow
    {
        public string ServerVersion { get; init; } = "";
        public string FullVersion { get; init; } = "";
        public string DatabaseName { get; init; } = "";
        public long DatabaseBytes { get; init; }
        public string DatabaseSize { get; init; } = "";
        public Instant ServerStartedAt { get; init; }
        public Instant? StatsResetAt { get; init; }
        public int Connections { get; init; }
        public int MaxConnections { get; init; }
        public double? CacheHitPercent { get; init; }
        public long Commits { get; init; }
        public long Rollbacks { get; init; }
        public long Deadlocks { get; init; }
        public long TempFiles { get; init; }
        public long TempBytes { get; init; }
    }

    private sealed class TableRow
    {
        public string Schema { get; init; } = "";
        public string Name { get; init; } = "";
        public long Rows { get; init; }
        public long TotalBytes { get; init; }
        public long TableBytes { get; init; }
        public long IndexBytes { get; init; }
        public long DeadRows { get; init; }
        public long SeqScans { get; init; }
        public long IndexScans { get; init; }
        public long Inserts { get; init; }
        public long Updates { get; init; }
        public long Deletes { get; init; }
        public Instant? LastVacuum { get; init; }
        public Instant? LastAnalyze { get; init; }
    }

    private sealed class IndexRow
    {
        public string Schema { get; init; } = "";
        public string Table { get; init; } = "";
        public string Name { get; init; } = "";
        public long Bytes { get; init; }
        public long Scans { get; init; }
        public bool IsUnique { get; init; }
        public bool IsPrimary { get; init; }
    }

    private sealed class ConnectionRow
    {
        public int Pid { get; init; }
        public string? Application { get; init; }
        public string? User { get; init; }
        public string? ClientAddress { get; init; }
        public string? State { get; init; }
        public string? WaitEvent { get; init; }
        public Instant? BackendStart { get; init; }
        public Instant? QueryStart { get; init; }
        public string? Query { get; init; }
    }

    private sealed class SettingRow
    {
        public string Name { get; init; } = "";
        public string Value { get; init; } = "";
        public string Source { get; init; } = "";
        public string Category { get; init; } = "";
    }

    private sealed class ExtensionRow
    {
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public string Schema { get; init; } = "";
    }
}
