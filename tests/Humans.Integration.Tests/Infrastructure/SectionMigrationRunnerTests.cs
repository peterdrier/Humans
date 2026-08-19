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
using Humans.SystemSettings.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Humans.Events.Data;
using Humans.Surveys.Data;
using Humans.GoogleIntegration.Data;
using Humans.Users.Data;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// Proves both branches of the real-up baseline mechanism
/// (nobodies-collective/Humans#858) against a real Postgres:
/// <list type="bullet">
/// <item><b>Fresh database</b> (section tables absent), for every section: the
/// baseline migration executes for real — tables, seed data, and history row
/// all appear.</item>
/// <item><b>Existing database</b> (tables present, section history empty): the
/// baseline is recorded as applied WITHOUT executing — no DDL error, no
/// duplicate seed, and the path is idempotent across repeated boots. Exercised
/// for <c>UsersDbContext</c>, the one context still facing a first mark-applied
/// boot in the wild: the historical root chain that used to build this fixture
/// was deleted at peel 15, and every other section's mark-applied path was
/// proven against the real chain in its own peel PR while the chain existed.
/// The fixture is the model's create script — equivalent to the chain-built
/// shape for Users because its baseline carries no raw-SQL blocks (peel-15
/// audit: all chain raw SQL against Users tables was data-only).</item>
/// <item><b>Schema equivalence</b> (Users): both paths yield the same physical
/// shape (columns, types, nullability, defaults, indexes, and constraints),
/// ignoring ordinal position, which legitimately differs between a table
/// evolved incrementally and one created from the model in one shot.</item>
/// </list>
/// Each new section adds one entry to <see cref="Sections"/>. The tables to
/// check come from the context model, not a literal list, so a table added to a
/// section context is covered without touching this file.
/// </summary>
public sealed class SectionMigrationRunnerTests(HumansTestDatabase database)
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
            // No seed probe: the old seeded holded_sync_states singleton moved to the Holded
            // section re-keyed and lazy-created; Finance's doc-sync state is lazy too.
            null),
        new(
            // After Finance, as in production's name-ordered migration: Finance's
            // HoldedMirrorMovesToHoldedSection drops holded_ledger_lines before this baseline
            // recreates it under Holded ownership.
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
            // Three seed rows, one per service type — probe a single reserved
            // Id so the count is 1 on both the fresh and mark-applied paths.
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
            // Six system-team seed rows — probe a single reserved Id so the
            // count is 1 on both the fresh and mark-applied paths.
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
                // Quoted: table names come from the EF model in their exact stored
                // case (e.g. DataProtectionKeys), and to_regclass lowercases bare names.
                (await ScalarAsync<bool>(connectionString,
                    $"""SELECT to_regclass('public."{table}"') IS NOT NULL"""))
                    .Should().BeTrue($"{section.Name}: {table} must exist after the baseline executes");
            }

            if (section.SeedProbeSql is not null)
            {
                (await ScalarAsync<long>(connectionString, section.SeedProbeSql))
                    .Should().Be(1, $"{section.Name}: the HasData seed must be inserted by the baseline");
            }

            // One history row per migration in the section's chain — the baseline plus any
            // post-baseline migrations (the runner executes those for real on every path).
            (await HistoryCountAsync(connectionString, section.Name)).Should().Be(migrationCount);
        }
    }

    [HumansFact]
    public async Task ExistingDatabase_UsersBaselineMarkedApplied_WithoutExecuting_AndIdempotent()
    {
        // Tables present, history empty — what QA/prod look like on the first boot
        // after peel 15 deploys (their Users tables are chain-built; the Users
        // history table does not exist yet).
        var connectionString = await CreateDatabaseAsync("existing_users");
        await CreateUsersSchemaWithoutHistoryAsync(connectionString);

        // The runner must record the baseline without executing it (a real execute
        // would fail on CREATE TABLE). Two passes prove idempotency — the second
        // boot sees the history row and no-ops.
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
        // Same fixture shape as ExistingDatabase_UsersBaselineMarkedApplied_WithoutExecuting:
        // tables present, no history table. GetPendingMigrationsAsync would log an Error
        // reading a history table that doesn't exist yet (nobodies-collective/Humans#1022);
        // CollectPendingFrontierAsync must fall back to the full migration set instead.
        var connectionString = await CreateDatabaseAsync("frontier_no_history");
        await CreateUsersSchemaWithoutHistoryAsync(connectionString);

        // EF.LogTo captures every command EF sends, including ones that fail — a regression
        // back to calling GetPendingMigrationsAsync unconditionally would still send a SELECT
        // against the (missing) history table and log it before the failure, so this catches
        // the regression even though the returned set alone can't distinguish it.
        var commandLog = new List<string>();
        var historyTable = SectionMigrationsHistory.TableFor<UsersDbContext>();
        var options = new DbContextOptionsBuilder<UsersDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseNodaTime();
                npgsql.MigrationsAssembly(typeof(UsersDbContext).Assembly.GetName().Name!);
                npgsql.MigrationsHistoryTable(historyTable);
            })
            .LogTo(commandLog.Add, Microsoft.Extensions.Logging.LogLevel.Debug)
            .Options;

        await using var db = new UsersDbContext(options);
        var expected = db.Database.GetMigrations()
            .Select(migration => $"{nameof(UsersDbContext)}:{migration}")
            .ToList();

        var frontier = await DatabaseMigrationHostedService.CollectPendingFrontierAsync(
            [db], TestContext.Current.CancellationToken);

        frontier.Should().BeEquivalentTo(expected);

        // The history table name only appears double-quoted when used as a SQL identifier
        // (a FROM/JOIN target) - the legitimate information_schema probe passes it as a
        // parameter value instead, which logs single-quoted.
        commandLog.Should().NotContain(
            entry => entry.Contains($"\"{historyTable}\"", StringComparison.Ordinal),
            $"the fallback must never query {historyTable} directly - that's the #1022 regression");
    }

    [HumansFact]
    public async Task BothPaths_ProduceEquivalentUsersSchema()
    {
        // Model create-script path (the stand-in for a chain-built database — valid
        // for Users because its baseline carries no raw-SQL blocks) vs the baseline
        // executing for real. Divergence here means the baseline migration's
        // operations no longer produce the model's schema.
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
                // The context's own assembly, not a literal: a section at G5 owns its
                // migrations (nobodies-collective/Humans#866), and this must match what
                // AddSectionDbContext configures at runtime.
                npgsql.MigrationsAssembly(typeof(TContext).Assembly.GetName().Name!);
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
        database.CreateEmptyDatabaseAsync(name, TestContext.Current.CancellationToken);

    /// <summary>
    /// Users tables and seeds from the model's create script, with no migration
    /// history — the shape QA/prod present on the first boot after peel 15.
    /// </summary>
    private static async Task CreateUsersSchemaWithoutHistoryAsync(string connectionString)
    {
        await using var db = CreateSectionContext<UsersDbContext>(connectionString);
        var script = db.Database.GenerateCreateScript();
        await db.Database.ExecuteSqlRawAsync(script, TestContext.Current.CancellationToken);
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
    /// (name, type, nullability, default), one per index definition, one per
    /// constraint (name, type, definition), and one per non-internal trigger
    /// definition, sorted. Constraints are compared because a missing or
    /// differently-shaped FK / CHECK is invisible to a columns-plus-indexes
    /// comparison, and a model-generated baseline that silently omits one would
    /// produce a fresh database that diverges from every chain-built database.
    /// Triggers are compared for the same reason: the Legal and AuditLog
    /// immutability triggers exist only as raw SQL in the baselines
    /// (nobodies-collective/Humans#858), so without this check the Sql block
    /// could drift from the chain-built schema silently.
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
