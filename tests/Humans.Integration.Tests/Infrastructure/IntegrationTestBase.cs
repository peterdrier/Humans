using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// Each test gets its own database and app host. The app resets with the database because its Singleton caches project
/// the database; resetting only the database leaves them pointing at deleted rows.
/// </summary>
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
            AllowAutoRedirect = false
        });
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();

        // Disposing the host closes the NpgsqlDataSource, so the drop doesn't fight live sessions.
        if (Factory is not null)
            await Factory.DisposeAsync();

        // Not the test's token: a timed-out test has already cancelled it, and must still clean up.
        await database.DropDatabaseAsync(_databaseName, CancellationToken.None);
    }
}
