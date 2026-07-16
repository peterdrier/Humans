using AwesomeAssertions;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// Proves both branches of the real-up baseline mechanism
/// (nobodies-collective/Humans#858) against a real Postgres, using the
/// SystemSettings section:
/// <list type="bullet">
/// <item><b>Fresh database</b> (section tables absent): the baseline migration
/// executes for real — tables, seed data, and history row all appear.</item>
/// <item><b>Existing database</b> (tables created by the historical
/// <c>HumansDbContext</c> chain, section history empty): the baseline is
/// recorded as applied WITHOUT executing — no DDL error, no duplicate seed,
/// and the path is idempotent across repeated boots.</item>
/// <item><b>Schema equivalence</b>: both paths yield the same physical shape
/// for the section's tables (columns, types, nullability, defaults, PK),
/// ignoring ordinal position, which legitimately differs between a table
/// evolved incrementally and one created from the model in one shot.</item>
/// </list>
/// </summary>
public sealed class SectionMigrationRunnerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    [HumansFact]
    public async Task FreshDatabase_BaselineExecutes_TablesSeedAndHistoryAppear()
    {
        var connectionString = await CreateDatabaseAsync("fresh_section_only");

        await using (var db = CreateSystemSettingsContext(connectionString))
        {
            await SectionMigrationRunner.MigrateAsync(
                db, "system_settings", NullLogger.Instance, TestContext.Current.CancellationToken);
        }

        (await ScalarAsync<bool>(connectionString,
            "SELECT to_regclass('public.system_settings') IS NOT NULL")).Should().BeTrue();
        (await ScalarAsync<long>(connectionString,
            """SELECT count(*) FROM system_settings WHERE "Key" = 'IsEmailSendingPaused'""")).Should().Be(1);
        (await ScalarAsync<long>(connectionString,
            """SELECT count(*) FROM "__EFMigrationsHistory_SystemSettings" """)).Should().Be(1);
    }

    [HumansFact]
    public async Task ExistingDatabase_BaselineMarkedApplied_WithoutExecuting_AndIdempotent()
    {
        var connectionString = await CreateDatabaseAsync("existing_old_chain");
        await MigrateOldChainAsync(connectionString);

        // The old chain already created system_settings + its seed row. The
        // runner must record the baseline without executing it (a real execute
        // would fail on CREATE TABLE and duplicate the seed).
        await using (var db = CreateSystemSettingsContext(connectionString))
        {
            await SectionMigrationRunner.MigrateAsync(
                db, "system_settings", NullLogger.Instance, TestContext.Current.CancellationToken);
        }

        (await ScalarAsync<long>(connectionString,
            """SELECT count(*) FROM system_settings WHERE "Key" = 'IsEmailSendingPaused'""")).Should().Be(1);
        (await ScalarAsync<long>(connectionString,
            """SELECT count(*) FROM "__EFMigrationsHistory_SystemSettings" """)).Should().Be(1);

        // Second boot: history row exists, so the runner goes straight to
        // MigrateAsync (no-op). Nothing duplicates, nothing throws.
        await using (var db = CreateSystemSettingsContext(connectionString))
        {
            await SectionMigrationRunner.MigrateAsync(
                db, "system_settings", NullLogger.Instance, TestContext.Current.CancellationToken);
        }

        (await ScalarAsync<long>(connectionString,
            """SELECT count(*) FROM "__EFMigrationsHistory_SystemSettings" """)).Should().Be(1);
    }

    [HumansFact]
    public async Task BothPaths_ProduceEquivalentSectionSchema()
    {
        var freshConnection = await CreateDatabaseAsync("equiv_fresh");
        await using (var db = CreateSystemSettingsContext(freshConnection))
        {
            await SectionMigrationRunner.MigrateAsync(
                db, "system_settings", NullLogger.Instance, TestContext.Current.CancellationToken);
        }

        var oldChainConnection = await CreateDatabaseAsync("equiv_old_chain");
        await MigrateOldChainAsync(oldChainConnection);

        var fromBaseline = await DescribeTableAsync(freshConnection, "system_settings");
        var fromOldChain = await DescribeTableAsync(oldChainConnection, "system_settings");

        fromBaseline.Should().BeEquivalentTo(fromOldChain);
    }

    private async Task<string> CreateDatabaseAsync(string name)
    {
        var admin = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {name}";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name }.ConnectionString;
    }

    private static SystemSettingsDbContext CreateSystemSettingsContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SystemSettingsDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseNodaTime();
                npgsql.MigrationsAssembly("Humans.Infrastructure");
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory_SystemSettings");
            })
            .Options;
        return new SystemSettingsDbContext(options);
    }

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
    /// (name, type, nullability, default) plus one line per PK/unique/index
    /// definition, sorted.
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

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }
}
