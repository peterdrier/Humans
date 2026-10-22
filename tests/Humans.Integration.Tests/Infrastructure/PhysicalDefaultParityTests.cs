using AwesomeAssertions;
using Humans.Base.Data;
using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Humans.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Humans.Users.Data;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// EF scaffolds a <c>DEFAULT</c> to backfill a new non-nullable column and never drops it, so chain-built databases
/// silently diverge from the model. A column has a model default if and only if the database has one; presence, not
/// rendered SQL, so Postgres formatting doesn't matter. Identity columns are exempt.
/// </summary>
/// <remarks>
/// When this fails on a new column: required columns need Peter's approval
/// (<c>memory/architecture/required-columns-need-approval.md</c>). Declare the approved default in the model or ship a
/// migration dropping the scaffolded one.
/// </remarks>
public sealed class PhysicalDefaultParityTests(HumansTestDatabase database)
{
    /// <summary>The pre-migration snapshot guards production; this throwaway database has nothing to protect.</summary>
    private static readonly Func<CancellationToken, Task> NoSnapshot = _ => Task.CompletedTask;

    [HumansFact]
    public async Task ChainBuiltDatabase_HasNoUndeclaredPhysicalColumnDefaults()
    {
        var connectionString = await CreateDatabaseAsync("default_parity");

        var contextTypes = RegisteredContextTypes()
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        contextTypes.Should().Contain(typeof(UsersDbContext));
        contextTypes.Count.Should().BeGreaterThan(1, "the section contexts must be discovered too");

        // Several properties can map to one column (TPH); the column has a default if any does.
        var modelColumns = new Dictionary<(string Table, string Column), bool>();
        foreach (var contextType in contextTypes)
        {
            await using var db = CreateContext(contextType, connectionString);
            // Any owned table is the sentinel: on a fresh database every one is absent, so each baseline runs for real.
            var sentinel = db.Model.GetEntityTypes()
                .Select(e => e.GetTableName())
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .First();
            await SectionMigrationRunner.MigrateAsync(
                db, sentinel, NullLogger.Instance, NoSnapshot, TestContext.Current.CancellationToken);

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
                    continue; // column mapped by no context (pending physical drop, EF history tables)

                var dbHas = reader.GetBoolean(2);
                if (dbHas == modelDeclares)
                    continue;

                mismatches.Add(dbHas
                    ? $"{key.Item1}.{key.Item2}: physical DEFAULT exists but the model declares none " +
                      "(scaffolded AddColumn default was never realigned)"
                    : $"{key.Item1}.{key.Item2}: model declares a default but the baseline-built database has none " +
                      "(missing migration for the configured default)");
            }
        }

        mismatches.Sort(StringComparer.Ordinal);
        string.Join(Environment.NewLine, mismatches).Should().BeEmpty(
            "every column's default must agree between the model and the baseline-built schema " +
            "— see PhysicalDefaultParityTests remarks for how to fix a divergence");
    }

    private static DbContext CreateContext(Type contextType, string connectionString)
    {
        var optionsBuilder = (DbContextOptionsBuilder)Activator.CreateInstance(
            typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
        optionsBuilder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsAssembly(contextType.Assembly.GetName().Name!);
            npgsql.MigrationsHistoryTable(SectionMigrationsHistory.TableFor(contextType));
        });
        return (DbContext)Activator.CreateInstance(contextType, optionsBuilder.Options)!;
    }

    /// <summary>The set the startup migrator uses, read off the DI descriptors without building a provider.</summary>
    private static IReadOnlyList<Type> RegisteredContextTypes()
    {
        var services = new ServiceCollection()
            .AddHumansPersistence()
            .AddDiscoveredSections(new ConfigurationBuilder().Build());

        return
        [
            .. services.Select(d => d.ImplementationInstance)
                .OfType<SectionDbContextRegistration>()
                .Select(r => r.ContextType),
        ];
    }

    private Task<string> CreateDatabaseAsync(string name) =>
        database.CreateEmptyDatabaseAsync(name, TestContext.Current.CancellationToken);
}
