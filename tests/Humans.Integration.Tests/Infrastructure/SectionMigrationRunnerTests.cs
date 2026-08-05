using AwesomeAssertions;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// Proves both branches of the real-up baseline mechanism
/// (nobodies-collective/Humans#858) against a real Postgres, for every peeled
/// section:
/// <list type="bullet">
/// <item><b>Fresh database</b> (section tables absent): the baseline migration
/// executes for real — tables, seed data, and history row all appear.</item>
/// <item><b>Existing database</b> (tables created by the historical
/// <c>HumansDbContext</c> chain, section history empty): the baseline is
/// recorded as applied WITHOUT executing — no DDL error, no duplicate seed,
/// and the path is idempotent across repeated boots.</item>
/// <item><b>Schema equivalence</b>: both paths yield the same physical shape
/// for the section's tables (columns, types, nullability, defaults, indexes,
/// and constraints), ignoring ordinal position, which legitimately differs
/// between a table evolved incrementally and one created from the model in one
/// shot.</item>
/// </list>
/// Each peel adds one entry to <see cref="Sections"/>. The tables to check come
/// from the context model, not a literal list, so a table added to a section
/// context is covered without touching this file.
/// </summary>
public sealed class SectionMigrationRunnerTests(HumansWebApplicationFactory factory)
{
    /// <summary>
    /// The pre-migration snapshot hook (nobodies-collective/Humans#845) is a production-boot
    /// concern; these tests migrate throwaway Testcontainers databases with nothing to protect.
    /// </summary>
    private static readonly Func<CancellationToken, Task> NoSnapshot = _ => Task.CompletedTask;

    private sealed record SectionCase(
        string Name,
        string SentinelTable,
        Func<string, DbContext> CreateContext,
        // Optional probe proving HasData seeds exist exactly once (null = section has no seed).
        string? SeedProbeSql);

    private static readonly SectionCase[] Sections =
    [
        new(
            "SystemSettings",
            "system_settings",
            CreateSectionContext<SystemSettingsDbContext>,
            """SELECT count(*) FROM system_settings WHERE "Key" = 'IsEmailSendingPaused'"""),
        new(
            "Containers",
            "containers",
            CreateSectionContext<ContainersDbContext>,
            null),
        new(
            "Agent",
            "agent_conversations",
            CreateSectionContext<AgentDbContext>,
            "SELECT count(*) FROM agent_settings"),
        new(
            "Expenses",
            "expense_reports",
            CreateSectionContext<ExpensesDbContext>,
            null),
        new(
            "Finance",
            "holded_expense_docs",
            CreateSectionContext<FinanceDbContext>,
            "SELECT count(*) FROM holded_sync_states"),
        new(
            "Surveys",
            "surveys",
            CreateSectionContext<SurveysDbContext>,
            null),
        new(
            "EventGuide",
            "events",
            CreateSectionContext<EventGuideDbContext>,
            """SELECT count(*) FROM event_categories WHERE "Slug" = 'workshop'"""),
    ];

    [HumansFact]
    public async Task FreshDatabase_BaselineExecutes_TablesSeedAndHistoryAppear()
    {
        foreach (var section in Sections)
        {
            var connectionString = await CreateDatabaseAsync($"fresh_{section.Name.ToLowerInvariant()}");

            List<string> tables;
            await using (var db = section.CreateContext(connectionString))
            {
                await SectionMigrationRunner.MigrateAsync(
                    db, section.SentinelTable, NullLogger.Instance, NoSnapshot, TestContext.Current.CancellationToken);
                tables = SectionTables(db);
            }

            foreach (var table in tables)
            {
                (await ScalarAsync<bool>(connectionString,
                    $"SELECT to_regclass('public.{table}') IS NOT NULL"))
                    .Should().BeTrue($"{section.Name}: {table} must exist after the baseline executes");
            }

            if (section.SeedProbeSql is not null)
            {
                (await ScalarAsync<long>(connectionString, section.SeedProbeSql))
                    .Should().Be(1, $"{section.Name}: the HasData seed must be inserted by the baseline");
            }

            (await HistoryCountAsync(connectionString, section.Name)).Should().Be(1);
        }
    }

    [HumansFact]
    public async Task ExistingDatabase_BaselineMarkedApplied_WithoutExecuting_AndIdempotent()
    {
        var connectionString = await CreateDatabaseAsync("existing_old_chain");
        await MigrateOldChainAsync(connectionString);

        // The old chain already created every section's tables (+ seeds). The
        // runner must record each baseline without executing it (a real execute
        // would fail on CREATE TABLE and duplicate seeds). Two passes prove
        // idempotency — the second boot sees history rows and no-ops.
        for (var boot = 1; boot <= 2; boot++)
        {
            foreach (var section in Sections)
            {
                await using var db = section.CreateContext(connectionString);
                await SectionMigrationRunner.MigrateAsync(
                    db, section.SentinelTable, NullLogger.Instance, NoSnapshot, TestContext.Current.CancellationToken);
            }
        }

        foreach (var section in Sections)
        {
            (await HistoryCountAsync(connectionString, section.Name))
                .Should().Be(1, $"{section.Name}: exactly one baseline history row after two boots");

            if (section.SeedProbeSql is not null)
            {
                (await ScalarAsync<long>(connectionString, section.SeedProbeSql))
                    .Should().Be(1, $"{section.Name}: mark-applied must not duplicate the seed");
            }
        }
    }

    [HumansFact]
    public async Task BothPaths_ProduceEquivalentSectionSchema()
    {
        var oldChainConnection = await CreateDatabaseAsync("equiv_old_chain");
        await MigrateOldChainAsync(oldChainConnection);

        foreach (var section in Sections)
        {
            var freshConnection = await CreateDatabaseAsync($"equiv_{section.Name.ToLowerInvariant()}");

            List<string> tables;
            await using (var db = section.CreateContext(freshConnection))
            {
                await SectionMigrationRunner.MigrateAsync(
                    db, section.SentinelTable, NullLogger.Instance, NoSnapshot, TestContext.Current.CancellationToken);
                tables = SectionTables(db);
            }

            foreach (var table in tables)
            {
                var fromBaseline = await DescribeTableAsync(freshConnection, table);
                var fromOldChain = await DescribeTableAsync(oldChainConnection, table);

                fromBaseline.Should().BeEquivalentTo(
                    fromOldChain, $"{section.Name}: {table} must be physically identical on both paths");
            }
        }
    }

    private static DbContext CreateSectionContext<TContext>(string connectionString)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseNodaTime();
                npgsql.MigrationsAssembly("Humans.Infrastructure");
                npgsql.MigrationsHistoryTable(SectionMigrationsHistory.TableFor<TContext>());
            })
            .Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    /// <summary>
    /// The section's tables, taken from the context model rather than a literal
    /// list, so a table added to a section context is checked automatically.
    /// </summary>
    private static List<string> SectionTables(DbContext db) =>
        db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A pristine database in the assembly's shared Postgres container — these tests
    /// drive the migration runner from scratch, so they need their own databases,
    /// not the app's.
    /// </summary>
    private Task<string> CreateDatabaseAsync(string name) =>
        factory.CreateDatabaseAsync(name, TestContext.Current.CancellationToken);

    private static async Task MigrateOldChainAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseNodaTime();
                npgsql.MigrationsAssembly("Humans.Infrastructure");
            })
            .Options;
        await using var db = new HumansDbContext(options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private static Task<long> HistoryCountAsync(string connectionString, string sectionName) =>
        ScalarAsync<long>(connectionString,
            $"""SELECT count(*) FROM "__EFMigrationsHistory_{sectionName}" """);

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (T?)result;
    }

    /// <summary>
    /// Ordinal-independent physical description of a table: one line per column
    /// (name, type, nullability, default), one per index definition, and one per
    /// constraint (name, type, definition), sorted. Constraints are compared
    /// because a missing or differently-shaped FK / CHECK is invisible to a
    /// columns-plus-indexes comparison, and a model-generated baseline that
    /// silently omits one would produce a fresh database that diverges from
    /// every chain-built database.
    /// </summary>
    private static async Task<List<string>> DescribeTableAsync(string connectionString, string table)
    {
        var rows = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = """
                SELECT column_name || '|' || data_type || '|' || is_nullable ||
                       '|' || coalesce(column_default, '') ||
                       '|' || coalesce(character_maximum_length::text, '')
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @table
                """;
            columns.Parameters.AddWithValue("table", table);
            await using var reader = await columns.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add("column:" + reader.GetString(0));
        }

        await using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText = """
                SELECT indexdef FROM pg_indexes
                WHERE schemaname = 'public' AND tablename = @table
                """;
            indexes.Parameters.AddWithValue("table", table);
            await using var reader = await indexes.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add("index:" + reader.GetString(0));
        }

        await using (var constraints = connection.CreateCommand())
        {
            constraints.CommandText = """
                SELECT c.conname::text || '|' || c.contype::text || '|' || pg_get_constraintdef(c.oid)
                FROM pg_constraint c
                JOIN pg_class t ON t.oid = c.conrelid
                JOIN pg_namespace n ON n.oid = t.relnamespace
                WHERE n.nspname = 'public' AND t.relname = @table
                """;
            constraints.Parameters.AddWithValue("table", table);
            await using var reader = await constraints.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add("constraint:" + reader.GetString(0));
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }
}
