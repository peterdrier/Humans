using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// The integration suite's Postgres container, registered once for the whole
/// assembly (see AssemblyFixtures.cs): one container and one migration pass per
/// <c>dotnet test</c> run.
/// </summary>
/// <remarks>
/// <para>
/// Container lifetime and app lifetime are separate concerns here.
/// <see cref="HumansWebApplicationFactory"/> is per test — that is what makes the
/// suite isolated (nobodies-collective/Humans#983) — so something longer-lived has
/// to own the container. This is it.
/// </para>
/// <para>
/// Migrations run exactly once, into <see cref="TemplateDatabase"/>, by booting one
/// throwaway app and then releasing it. Every per-test database is a
/// <c>CREATE DATABASE … TEMPLATE</c> copy of that, which is roughly fifty times
/// cheaper than migrating from scratch.
/// </para>
/// </remarks>
public sealed class HumansTestDatabase : IAsyncLifetime
{
    /// <summary>Migrated once at assembly start; every per-test database is a copy of it. Never connected to afterwards — a template must be quiescent to be cloned.</summary>
    private const string TemplateDatabase = "humans_template";

    private PostgreSqlContainer? _postgres;
    private string _adminConnectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        // Tests run in parallel and each one holds an app host with its own connection
        // pool, so the ceiling is xUnit's thread count (the machine's core count) times
        // a handful — well past Postgres' default 100 on a big CI runner. Raising it
        // here keeps the suite's parallelism from being silently capped by the box.
        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithCommand("-c", "max_connections=500")
            .Build();
        await _postgres.StartAsync(ct);
        _adminConnectionString = _postgres.GetConnectionString();

        var templateConnectionString = await CreateEmptyDatabaseAsync(TemplateDatabase, ct);

        // Boot one app against the template purely to run the migration chain, then
        // let it go: CREATE DATABASE … TEMPLATE refuses to copy a database that still
        // has sessions on it.
        await using (var migrator = new HumansWebApplicationFactory(templateConnectionString))
        {
            await migrator.InitializeAsync();
        }

        await TerminateBackendsAsync(TemplateDatabase, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    /// <summary>
    /// Copies the migrated template into a new database and returns its connection
    /// string. This is how a test gets a database nobody else can write to.
    /// </summary>
    public async Task<string> CloneTemplateAsync(string name, CancellationToken ct)
    {
        await ExecuteAdminAsync($"CREATE DATABASE {Quote(name)} TEMPLATE {Quote(TemplateDatabase)}", ct);
        return ConnectionStringFor(name);
    }

    /// <summary>
    /// Creates an empty, unmigrated database. The migration-mechanics tests
    /// (<see cref="PhysicalDefaultParityTests"/>, <see cref="SectionMigrationRunnerTests"/>)
    /// need to migrate from nothing, so a template copy is no use to them.
    /// </summary>
    public async Task<string> CreateEmptyDatabaseAsync(string name, CancellationToken ct)
    {
        await ExecuteAdminAsync($"CREATE DATABASE {Quote(name)}", ct);
        return ConnectionStringFor(name);
    }

    /// <summary>
    /// Drops a per-test database. Called after every test — without it a full run
    /// leaves ~120 schema copies in the container.
    /// </summary>
    public async Task DropDatabaseAsync(string name, CancellationToken ct)
    {
        await TerminateBackendsAsync(name, ct);
        await ExecuteAdminAsync($"DROP DATABASE IF EXISTS {Quote(name)}", ct);
    }

    private string ConnectionStringFor(string name) =>
        new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = name }.ConnectionString;

    /// <summary>
    /// A connection string naming a database that is never created — the container is
    /// reachable but the catalog isn't, the shape of a PR preview whose
    /// <c>preview-db.yml</c> clone hasn't run yet (nobodies-collective/Humans#1060).
    /// Never touches the server.
    /// </summary>
    public string ConnectionStringForMissingDatabase(string name) => ConnectionStringFor(name);

    private async Task ExecuteAdminAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task TerminateBackendsAsync(string name, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE datname = @d AND pid <> pg_backend_pid()";
        command.Parameters.AddWithValue("d", name);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Database names are composed in this assembly, never from test input; quoting keeps a name with a capital letter or a hyphen working.</summary>
    private static string Quote(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
