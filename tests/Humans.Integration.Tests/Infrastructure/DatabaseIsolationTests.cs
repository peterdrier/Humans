using Humans.Teams.Data;
using Humans.Teams.Domain;
using System.Net;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>Fails on a shared database, passes on a per-test one.</summary>
/// <remarks>
/// xUnit gives each theory case its own instance and so its own database; the two cases detect each other's bleed.
/// The fixed slug is uniquely indexed, so a shared database fails the write. The dev-login check catches the opposite
/// mistake, a database reset under a live app whose caches still hold deleted ids.
/// </remarks>
public sealed class DatabaseIsolationTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>Deliberately fixed, not GUID-suffixed.</summary>
    private const string SharedSlug = "issue-983-isolation-marker";

    [HumansTheory(Timeout = 60_000)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EachTestGetsAnEmptyDatabaseAndACoherentApp(int caseNumber)
    {
        var ct = TestContext.Current.CancellationToken;

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamsDbContext>();

            db.Teams.Add(new Team
            {
                Id = Guid.NewGuid(),
                Name = $"Isolation probe {caseNumber}",
                Slug = SharedSlug
            });
            await db.SaveChangesAsync(ct);

            var written = await db.Teams.AsNoTracking().CountAsync(t => t.Slug == SharedSlug, ct);

            written.Should().Be(1,
                "each theory case gets its own database — on a shared one the second case collides with the first case's row");
        }

        var login = await Client.GetAsync("/dev/login/volunteer", ct);
        login.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "the app's caches must stay coherent with the database it is bound to");
    }
}
