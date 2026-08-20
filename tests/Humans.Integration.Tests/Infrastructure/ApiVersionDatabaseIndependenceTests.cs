using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// Coverage for nobodies-collective/Humans#1060 criterion 4: <c>/api/version</c> must stay
/// answerable without a database, so it remains a usable liveness probe rather than another
/// 404 when something DB-related is unhealthy. The endpoint already reads only assembly
/// metadata, so this pins that against regression — a database outage after boot must never
/// take it down with everything else.
/// </summary>
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

        // Sanity: the endpoint works while the database is healthy.
        var healthy = await client.GetAsync("/api/version", ct);
        healthy.StatusCode.Should().Be(HttpStatusCode.OK);

        // Drop the database out from under the already-running app — the shape of a
        // database that disappears after boot (restart, credential rotation, network
        // partition). If /api/version ever gained a database dependency, this is what
        // would catch it: everything else on the site would degrade too, but the
        // liveness probe must not.
        await database.DropDatabaseAsync(dbName, ct);

        var afterOutage = await client.GetAsync("/api/version", ct);
        afterOutage.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
