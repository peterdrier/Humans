using Humans.Shifts.Data;
using Humans.Camps.Data;
using Humans.Auth.Data;
using Humans.AuditLog.Data;
using Humans.Teams.Data;
using Humans.Email.Data;
using Humans.Tickets.Data;
using Humans.Consent.Data;
using Humans.Notifications.Data;
using Humans.Budget.Data;
using Humans.Calendar.Data;
using Humans.Campaigns.Data;
using AwesomeAssertions;
using Humans.Agent.Data;
using Humans.Base.Data;
using Humans.Web.Data;
using Humans.Base.Hosting;
using Humans.CityPlanning.Data;
using Humans.Containers.Data;
using Humans.Expenses.Data;
using Humans.Feedback.Data;
using Humans.Governance.Data;
using Humans.Issues.Data;
using Humans.Finance.Data;
using Humans.Gate.Data;
using Humans.Holded.Data;
using Humans.Store.Data;
using Humans.Settings.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Humans.Events.Data;
using Humans.Surveys.Data;
using Humans.GoogleIntegration.Data;
using Humans.Users.Data;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// Both branches of the baseline mechanism against real Postgres: on a fresh database every section's baseline runs
/// for real; on an existing one (tables present, history empty) the Users baseline is recorded without executing,
/// idempotently. Users is the one context that can still meet that first boot. Both paths must yield the same
/// physical schema, ignoring column order. Tables come from the context model, so new ones are covered.
/// </summary>
public sealed class SectionMigrationRunnerTests(HumansTestDatabase database)
{
    /// <summary>The pre-migration snapshot guards production; these throwaway databases have nothing to protect.</summary>
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
            "Settings",
            "system_settings",
            CreateSectionContext<SettingsDbContext>,
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
            // No seed probe: Holded's sync state and Finance's doc-sync state are lazy-created.
            null),
        new(
            // After Finance, as in production's name order: Finance drops holded_ledger_lines before this baseline
            // recreates it.
            "Holded",
            "holded_accounts",
            CreateSectionContext<HoldedDbContext>,
            null),
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
        new(
            "Store",
            "store_orders",
            CreateSectionContext<StoreDbContext>,
            null),
        new(
            "Auth",
            "role_assignments",
            CreateSectionContext<AuthDbContext>,
            null),
        new(
            "Email",
            "email_outbox_messages",
            CreateSectionContext<EmailDbContext>,
            null),
        new(
            "Calendar",
            "calendar_events",
            CreateSectionContext<CalendarDbContext>,
            null),
        new(
            "Notifications",
            "notifications",
            CreateSectionContext<NotificationsDbContext>,
            null),
        new(
            "Issues",
            "issues",
            CreateSectionContext<IssuesDbContext>,
            null),
        new(
            "Governance",
            "applications",
            CreateSectionContext<GovernanceDbContext>,
            null),
        new(
            "Campaigns",
            "campaigns",
            CreateSectionContext<CampaignsDbContext>,
            null),
        new(
            "GoogleIntegration",
            "google_resources",
            CreateSectionContext<GoogleIntegrationDbContext>,
            // Three seed rows: probe one reserved Id so the count is 1 on both paths.
            """SELECT count(*) FROM sync_service_settings WHERE "Id" = '00000000-0000-0000-0002-000000000002'"""),
        new(
            "Tickets",
            "ticket_orders",
            CreateSectionContext<TicketsDbContext>,
            "SELECT count(*) FROM ticket_sync_state"),
        new(
            "Feedback",
            "feedback_reports",
            CreateSectionContext<FeedbackDbContext>,
            null),
        new(
            "CityPlanning",
            "city_planning_settings",
            CreateSectionContext<CityPlanningDbContext>,
            null),
        new(
            "Budget",
            "budget_years",
            CreateSectionContext<BudgetDbContext>,
            null),
        new(
            "Camps",
            "camps",
            CreateSectionContext<CampsDbContext>,
            "SELECT count(*) FROM camp_settings"),
        new(
            "Gate",
            "gate_settings",
            CreateSectionContext<GateDbContext>,
            null),
        new(
            "System",
            "DataProtectionKeys",
            CreateSectionContext<SystemDbContext>,
            null),
        new(
            "Legal",
            "legal_documents",
            CreateSectionContext<LegalDbContext>,
            null),
        new(
            "AuditLog",
            "audit_log",
            CreateSectionContext<AuditLogDbContext>,
            null),
        new(
            "Shifts",
            "shifts",
            CreateSectionContext<ShiftsDbContext>,
            "SELECT count(*) FROM shift_tags WHERE \"Id\" = '00000000-0000-0000-0003-000000000001'"),
        new(
            "Teams",
            "teams",
            CreateSectionContext<TeamsDbContext>,
            // Six seed rows: probe one reserved Id so the count is 1 on both paths.
            "SELECT count(*) FROM teams WHERE \"Id\" = '00000000-0000-0000-0001-000000000001'"),
        new(
            "Users",
            "users",
            CreateSectionContext<UsersDbContext>,
            null),
    ];

    [HumansFact]
    public async Task FreshDatabase_BaselineExecutes_TablesSeedAndHistoryAppear()
    {
        foreach (var section in Sections)
        {
            var connectionString = await CreateDatabaseAsync($"fresh_{section.Name.ToLowerInvariant()}");

            List<string> tables;
            int migrationCount;
            await using (var db = section.CreateContext(connectionString))
            {
                await SectionMigrationRunner.MigrateAsync(
                    db, section.SentinelTable, NullLogger.Instance, NoSnapshot, TestContext.Current.CancellationToken);
                tables = SectionTables(db);
                migrationCount = db.Database.GetMigrations().Count();
            }

            foreach (var table in tables)
            {
                // Quoted: model table names keep their case (DataProtectionKeys), and to_regclass lowercases bare names.
                (await ScalarAsync<bool>(connectionString,
                    $"""SELECT to_regclass('public."{table}"') IS NOT NULL"""))
                    .Should().BeTrue($"{section.Name}: {table} must exist after the baseline executes");
            }

            if (section.SeedProbeSql is not null)
            {
                (await ScalarAsync<long>(connectionString, section.SeedProbeSql))
                    .Should().Be(1, $"{section.Name}: the HasData seed must be inserted by the baseline");
            }

            // The baseline plus any post-baseline migrations, which run for real on every path.
            (await HistoryCountAsync(connectionString, section.Name)).Should().Be(migrationCount);
        }
    }

    [HumansFact]
    public async Task ExistingDatabase_UsersBaselineMarkedApplied_WithoutExecuting_AndIdempotent()
    {
        // Tables present, history empty. Baseline-only, so post-baseline migrations still execute for real.
        var connectionString = await CreateDatabaseAsync("existing_users");
        await CreateUsersSchemaFromBaselineOnlyAsync(connectionString);

        // A real execute would fail on CREATE TABLE. The second boot proves idempotency.
        for (var boot = 1; boot <= 2; boot++)
        {
            await using var db = CreateSectionContext<UsersDbContext>(connectionString);
            await SectionMigrationRunner.MigrateAsync(
                db, "users", NullLogger.Instance, NoSnapshot, TestContext.Current.CancellationToken);
        }

        int migrationCount;
        await using (var db = CreateSectionContext<UsersDbContext>(connectionString))
        {
            migrationCount = db.Database.GetMigrations().Count();
        }

        (await HistoryCountAsync(connectionString, "Users"))
            .Should().Be(migrationCount, "Users: one history row per migration after two boots");
    }

    [HumansFact]
    public async Task CollectPendingFrontier_HistoryTableAbsent_ReturnsFullMigrationSet()
    {
        // No history table: GetPendingMigrationsAsync would log an Error reading it, so CollectPendingFrontierAsync must
        // fall back to the full migration set.
        var connectionString = await CreateDatabaseAsync("frontier_no_history");
        await CreateUsersSchemaFromBaselineOnlyAsync(connectionString);

        // LogTo captures failing commands too, so a SELECT against the missing history table shows here even though
        // the returned set can't tell the difference.
        var commandLog = new List<string>();
        var historyTable = SectionMigrationsHistory.TableFor<UsersDbContext>();
        var options = new DbContextOptionsBuilder<UsersDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseNodaTime();
                npgsql.MigrationsAssembly(typeof(UsersDbContext).Assembly.GetName().Name!);
                npgsql.MigrationsHistoryTable(historyTable);
            })
            .LogTo(commandLog.Add)
            .Options;

        await using var db = new UsersDbContext(options);
        var expected = db.Database.GetMigrations()
            .Select(migration => $"{nameof(UsersDbContext)}:{migration}")
            .ToList();

        var frontier = await DatabaseMigrationHostedService.CollectPendingFrontierAsync(
            [db], TestContext.Current.CancellationToken);

        frontier.Should().BeEquivalentTo(expected);

        // Used as an identifier the history table is double-quoted; the information_schema probe passes it
        // single-quoted as a parameter.
        commandLog.Should().NotContain(
            entry => entry.Contains($"\"{historyTable}\"", StringComparison.Ordinal),
            $"the fallback must never query {historyTable} directly - that's the #1022 regression");
    }

    [HumansFact]
    public async Task BothPaths_ProduceEquivalentUsersSchema()
    {
        // Model create-script (the stand-in for a chain-built database) vs the baseline executing for real.
        var scriptConnection = await CreateDatabaseAsync("equiv_users_script");
        await CreateUsersSchemaWithoutHistoryAsync(scriptConnection);

        var freshConnection = await CreateDatabaseAsync("equiv_users_baseline");

        List<string> tables;
        await using (var db = CreateSectionContext<UsersDbContext>(freshConnection))
        {
            await SectionMigrationRunner.MigrateAsync(
                db, "users", NullLogger.Instance, NoSnapshot, TestContext.Current.CancellationToken);
            tables = SectionTables(db);
        }

        foreach (var table in tables)
        {
            var fromBaseline = await DescribeTableAsync(freshConnection, table);
            var fromScript = await DescribeTableAsync(scriptConnection, table);

            fromBaseline.Should().BeEquivalentTo(
                fromScript, $"Users: {table} must be physically identical on both paths");
        }
    }

    private static DbContext CreateSectionContext<TContext>(string connectionString)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseNodaTime();
                // Must match what AddSectionDbContext configures at runtime.
                npgsql.MigrationsAssembly(typeof(TContext).Assembly.GetName().Name!);
                npgsql.MigrationsHistoryTable(SectionMigrationsHistory.TableFor<TContext>());
            })
            .Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    private static List<string> SectionTables(DbContext db) =>
        db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private Task<string> CreateDatabaseAsync(string name) =>
        database.CreateEmptyDatabaseAsync(name, TestContext.Current.CancellationToken);

    /// <summary>
    /// The current cumulative model, no history. For "tables exist, history empty" use
    /// <see cref="CreateUsersSchemaFromBaselineOnlyAsync"/>, which leaves post-baseline migrations to run.
    /// </summary>
    private static async Task CreateUsersSchemaWithoutHistoryAsync(string connectionString)
    {
        await using var db = CreateSectionContext<UsersDbContext>(connectionString);
        var script = db.Database.GenerateCreateScript();
        await db.Database.ExecuteSqlRawAsync(script, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The baseline's own DDL via <see cref="IMigrationsSqlGenerator"/>, not <see cref="IMigrator.GenerateScript"/>,
    /// which would also create and fill the history table.
    /// </summary>
    private static async Task CreateUsersSchemaFromBaselineOnlyAsync(string connectionString)
    {
        await using var db = CreateSectionContext<UsersDbContext>(connectionString);
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        var baselineId = db.Database.GetMigrations().First();
        var baseline = migrationsAssembly.CreateMigration(
            migrationsAssembly.Migrations[baselineId], db.Database.ProviderName!);
        var sqlGenerator = db.GetService<IMigrationsSqlGenerator>();
        var commands = sqlGenerator.Generate(baseline.UpOperations, baseline.TargetModel);
        foreach (var command in commands)
            await db.Database.ExecuteSqlRawAsync(command.CommandText, TestContext.Current.CancellationToken);
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
    /// Column order ignored. Constraints and triggers are included because a missing FK, CHECK or raw-SQL immutability
    /// trigger is invisible to a columns-and-indexes comparison.
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

        await using (var triggers = connection.CreateCommand())
        {
            triggers.CommandText = """
                SELECT pg_get_triggerdef(tg.oid)
                FROM pg_trigger tg
                JOIN pg_class t ON t.oid = tg.tgrelid
                JOIN pg_namespace n ON n.oid = t.relnamespace
                WHERE n.nspname = 'public' AND t.relname = @table AND NOT tg.tgisinternal
                """;
            triggers.Parameters.AddWithValue("table", table);
            await using var reader = await triggers.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add("trigger:" + reader.GetString(0));
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }
}
