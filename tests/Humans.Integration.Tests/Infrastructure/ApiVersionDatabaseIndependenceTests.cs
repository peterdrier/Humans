using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary><c>/api/version</c> answers without a database, so it stays a usable liveness probe.</summary>
public sealed class ApiVersionDatabaseIndependenceTests(HumansTestDatabase database)
{
    [HumansFact]
    public async Task ApiVersion_RespondsAfterDatabaseBecomesUnreachable()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbName = $"test_{Guid.NewGuid():N}";
        var connectionString = await database.CloneTemplateAsync(dbName, ct);

        await using var factory = new HumansWebApplicationFactory(connectionString);
        await factory.InitializeAsync();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var healthy = await client.GetAsync("/api/version", ct);
        healthy.StatusCode.Should().Be(HttpStatusCode.OK);

        // A database that disappears after boot.
        await database.DropDatabaseAsync(dbName, ct);

        var afterOutage = await client.GetAsync("/api/version", ct);
        afterOutage.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
