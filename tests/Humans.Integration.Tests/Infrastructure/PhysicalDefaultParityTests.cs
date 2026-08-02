using AwesomeAssertions;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// Guards against the §5.1 "scaffolded physical default" divergence class
/// (nobodies-collective/Humans#858, found the hard way during the Gate peel):
/// when EF adds a non-nullable column to an existing table it scaffolds a
/// <c>DEFAULT</c> clause so existing rows can be backfilled — and never drops
/// it. The physical default then persists in every chain-built database while
/// the model (correctly) declares none, silently diverging fresh-baseline
/// schema from old-chain schema and walling off that section's future peel.
/// </summary>
/// <remarks>
/// The check is <b>presence parity</b> per column: the model declares a default
/// (<c>HasDefaultValue</c>/<c>HasDefaultValueSql</c>) if and only if the
/// chain-built database has one. Comparing presence rather than rendered SQL
/// keeps the test immune to Postgres literal-formatting noise while still
/// catching both divergence directions. Identity columns are exempt (their
/// generation is not a column default). When this test fails on a new column:
/// declare the default in the model too (see the bool-sentinel rules in
/// <c>.claude/agents/ef-migration-reviewer.md</c>) or ship a realignment
/// migration dropping the scaffolded default — never leave the two disagreeing.
/// </remarks>
public sealed class PhysicalDefaultParityTests : IAsyncLifetime
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
    public async Task ChainBuiltDatabase_HasNoUndeclaredPhysicalColumnDefaults()
    {
        var connectionString = await CreateDatabaseAsync("default_parity");

        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseNodaTime();
                npgsql.MigrationsAssembly("Humans.Infrastructure");
            })
            .Options;

        // (table, column) -> model declares a default. Multiple properties can
        // map to one column (TPH); the column declares a default if any does.
        var modelColumns = new Dictionary<(string Table, string Column), bool>();
        await using (var db = new HumansDbContext(options))
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            foreach (var entity in db.Model.GetEntityTypes())
            {
                var tableName = entity.GetTableName();
                if (tableName is null)
                    continue;

                var table = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetColumnName(table) is not { } column)
                        continue;

                    var declares = property.GetDefaultValueSql(table) is not null
                        || property.TryGetDefaultValue(table, out _);
                    var key = (tableName, column);
                    modelColumns[key] = modelColumns.TryGetValue(key, out var existing)
                        ? existing || declares
                        : declares;
                }
            }
        }

        var mismatches = new List<string>();
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT table_name, column_name,
                       column_default IS NOT NULL AND is_identity = 'NO' AS has_default
                FROM information_schema.columns
                WHERE table_schema = 'public'
                """;
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!modelColumns.TryGetValue(key, out var modelDeclares))
                    continue; // column not mapped by HumansDbContext (peeled section, pending physical drop, EF history)

                var dbHas = reader.GetBoolean(2);
                if (dbHas == modelDeclares)
                    continue;

                mismatches.Add(dbHas
                    ? $"{key.Item1}.{key.Item2}: physical DEFAULT exists but the model declares none " +
                      "(scaffolded AddColumn default was never realigned)"
                    : $"{key.Item1}.{key.Item2}: model declares a default but the chain-built database has none " +
                      "(missing migration for the configured default)");
            }
        }

        mismatches.Sort(StringComparer.Ordinal);
        string.Join(Environment.NewLine, mismatches).Should().BeEmpty(
            "every column's default must agree between the model and the chain-built schema " +
            "— see PhysicalDefaultParityTests remarks for how to fix a divergence");
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
}
