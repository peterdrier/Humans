using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// One Postgres container for the assembly, outliving the per-test app hosts. Migrations run once into
/// <see cref="TemplateDatabase"/>; each test database is a <c>CREATE DATABASE … TEMPLATE</c> copy, about fifty times
/// cheaper than migrating.
/// </summary>
public sealed class HumansTestDatabase : IAsyncLifetime
{
    /// <summary>Never connected to after migration: a template must be quiescent to be cloned.</summary>
    private const string TemplateDatabase = "humans_template";

    private PostgreSqlContainer? _postgres;
    private string _adminConnectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        // Opt-in suite: when the gate skips every fact, a container start could only fail the run.
        if (IntegrationTestGate.SkipReason is not null)
            return;

        var ct = TestContext.Current.CancellationToken;

        // Parallel tests each hold an app host with its own pool, which on a big runner passes Postgres' default 100.
        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithCommand("-c", "max_connections=500")
            .Build();
        await _postgres.StartAsync(ct);
        _adminConnectionString = _postgres.GetConnectionString();

        var templateConnectionString = await CreateEmptyDatabaseAsync(TemplateDatabase, ct);

        // Released before cloning: CREATE DATABASE … TEMPLATE refuses a database with live sessions.
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

    public async Task<string> CloneTemplateAsync(string name, CancellationToken ct)
    {
        await ExecuteAdminAsync($"CREATE DATABASE {Quote(name)} TEMPLATE {Quote(TemplateDatabase)}", ct);
        return ConnectionStringFor(name);
    }

    /// <summary>For the migration-mechanics tests, which migrate from nothing.</summary>
    public async Task<string> CreateEmptyDatabaseAsync(string name, CancellationToken ct)
    {
        await ExecuteAdminAsync($"CREATE DATABASE {Quote(name)}", ct);
        return ConnectionStringFor(name);
    }

    /// <summary>Called after every test, or a full run leaves ~120 schema copies in the container.</summary>
    public async Task DropDatabaseAsync(string name, CancellationToken ct)
    {
        await TerminateBackendsAsync(name, ct);
        await ExecuteAdminAsync($"DROP DATABASE IF EXISTS {Quote(name)}", ct);
    }

    private string ConnectionStringFor(string name) =>
        new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = name }.ConnectionString;

    /// <summary>
    /// A database that is never created: a reachable server without the catalog, like a PR preview before its clone
    /// runs. Never touches the server.
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

    /// <summary>Names are composed here, never from test input; quoting keeps capitals and hyphens working.</summary>
    private static string Quote(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
