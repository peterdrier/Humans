using AwesomeAssertions;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Humans.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
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
/// <para>
/// The check is <b>presence parity</b> per column: the model declares a default
/// (<c>HasDefaultValue</c>/<c>HasDefaultValueSql</c>) if and only if the
/// chain-built database has one. Comparing presence rather than rendered SQL
/// keeps the test immune to Postgres literal-formatting noise while still
/// catching both divergence directions. Identity columns are exempt (their
/// generation is not a column default).
/// </para>
/// <para>
/// Coverage spans <b>every</b> DbContext DI registers — not every one in
/// Humans.Infrastructure, which stopped being the same set once sections began
/// moving into their own projects (nobodies-collective/Humans#866, G5) and would
/// have dropped a moved section silently: the main pile migrates its historical chain, then each
/// section context runs through <see cref="SectionMigrationRunner"/> exactly
/// as production boot does — so a scaffolded default introduced by a
/// <em>section's own</em> migration chain fails here too, and future peels are
/// covered without touching this file. When this test fails on a new column:
/// required columns need Peter's approval in the first place
/// (<c>memory/architecture/required-columns-need-approval.md</c>); declare the
/// approved default in the model (see the bool-sentinel rules in
/// <c>.claude/agents/ef-migration-reviewer.md</c>) or ship a realignment
/// migration dropping the scaffolded default — never leave the two disagreeing.
/// </para>
/// </remarks>
public sealed class PhysicalDefaultParityTests(HumansTestDatabase database)
{
    /// <summary>
    /// The pre-migration snapshot hook (nobodies-collective/Humans#845) is a production-boot
    /// concern; this test migrates a throwaway test database with nothing to protect.
    /// </summary>
    private static readonly Func<CancellationToken, Task> NoSnapshot = _ => Task.CompletedTask;

    [HumansFact]
    public async Task ChainBuiltDatabase_HasNoUndeclaredPhysicalColumnDefaults()
    {
        var connectionString = await CreateDatabaseAsync("default_parity");

        var contextTypes = RegisteredContextTypes()
            .OrderBy(t => t == typeof(HumansDbContext) ? 0 : 1) // historical chain provisions everything first
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        contextTypes.Should().Contain(typeof(HumansDbContext));
        contextTypes.Count.Should().BeGreaterThan(1, "the section contexts must be discovered too");

        // (table, column) -> any mapping context declares a default. Multiple
        // properties can map to one column (TPH); it declares if any does.
        var modelColumns = new Dictionary<(string Table, string Column), bool>();
        foreach (var contextType in contextTypes)
        {
            await using var db = CreateContext(contextType, connectionString);
            if (contextType == typeof(HumansDbContext))
            {
                await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }
            else
            {
                // Any owned table works as the existence sentinel: the chain
                // just created them all, so the runner takes the same
                // mark-applied path it takes on production, then applies any
                // post-baseline section migrations for real.
                var sentinel = db.Model.GetEntityTypes()
                    .Select(e => e.GetTableName())
                    .OfType<string>()
                    .Order(StringComparer.Ordinal)
                    .First();
                await SectionMigrationRunner.MigrateAsync(
                    db, sentinel, NullLogger.Instance, NoSnapshot, TestContext.Current.CancellationToken);
            }

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
                    : $"{key.Item1}.{key.Item2}: model declares a default but the chain-built database has none " +
                      "(missing migration for the configured default)");
            }
        }

        mismatches.Sort(StringComparer.Ordinal);
        string.Join(Environment.NewLine, mismatches).Should().BeEmpty(
            "every column's default must agree between the model and the chain-built schema " +
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
            if (contextType != typeof(HumansDbContext))
                npgsql.MigrationsHistoryTable(SectionMigrationsHistory.TableFor(contextType));
        });
        return (DbContext)Activator.CreateInstance(contextType, optionsBuilder.Options)!;
    }

    /// <summary>
    /// The authoritative context list — the same set the startup migrator consumes.
    /// Section contexts arrive via <c>AddSectionDbContext</c>, whether that call sits
    /// in <c>AddHumansPersistence</c> or in a moved section's <c>ISection.Register</c>.
    /// No provider is built: the registrations are read straight off the descriptors.
    /// </summary>
    private static IReadOnlyList<Type> RegisteredContextTypes()
    {
        var services = new ServiceCollection()
            .AddHumansPersistence(enableDeveloperDiagnostics: false)
            .AddDiscoveredSections(new ConfigurationBuilder().Build());

        return
        [
            typeof(HumansDbContext),
            .. services.Select(d => d.ImplementationInstance)
                .OfType<SectionDbContextRegistration>()
                .Select(r => r.ContextType),
        ];
    }

    /// <summary>
    /// A pristine database in the assembly's shared Postgres container — this test
    /// migrates from scratch, so it needs its own database, not the app's.
    /// </summary>
    private Task<string> CreateDatabaseAsync(string name) =>
        database.CreateEmptyDatabaseAsync(name, TestContext.Current.CancellationToken);
}
