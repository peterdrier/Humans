using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// Base for every integration test class. Gives each test its own database and its
/// own app host, so no test can observe another test's writes
/// (nobodies-collective/Humans#983).
/// </summary>
/// <remarks>
/// <para>
/// The app is reset alongside the database on purpose. The app's Singleton caches
/// are a projection of the database, so resetting only the database would leave the
/// app asserting rows that no longer exist — the failure mode that took the
/// TRUNCATE approach down. See <c>docs/testing/test-system-reliability.md</c>.
/// </para>
/// <para>
/// A test therefore starts from the migrated schema and nothing else, and pays a
/// database clone plus an app boot for it — around a third of a second.
/// </para>
/// </remarks>
public abstract class IntegrationTestBase(HumansTestDatabase database) : IAsyncLifetime
{
    protected HttpClient Client { get; private set; } = null!;
    protected HumansWebApplicationFactory Factory { get; private set; } = null!;

    private readonly string _databaseName = $"test_{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        var connectionString = await database.CloneTemplateAsync(_databaseName, ct);
        Factory = new HumansWebApplicationFactory(connectionString);
        await Factory.InitializeAsync();

        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // Don't follow redirects so we can assert on redirect responses
            AllowAutoRedirect = false
        });
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();

        // Disposing the host closes the NpgsqlDataSource, without which the drop
        // below would have to fight live sessions for the database.
        if (Factory is not null)
            await Factory.DisposeAsync();

        // Not the test's token: a test that timed out has already cancelled it, and
        // that is precisely the run that must still clean up after itself.
        await database.DropDatabaseAsync(_databaseName, CancellationToken.None);
    }
}
