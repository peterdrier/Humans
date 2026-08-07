using System.Net;
using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// The regression guard for per-test isolation (nobodies-collective/Humans#983).
/// Fails on a shared database; passes on a per-test one.
/// </summary>
/// <remarks>
/// <para>
/// A two-case theory is the whole trick: xUnit gives each case its own class
/// instance, so each gets its own <see cref="IntegrationTestBase.InitializeAsync"/>
/// and therefore its own database. No test ordering is assumed and no sibling class
/// has to cooperate — the two cases are each other's bleed detector.
/// </para>
/// <para>
/// Both halves of the mechanism are checked, because isolating only one of them is
/// how the two earlier attempts failed. Inserting a team under a <em>fixed</em> slug
/// catches a shared database: <c>Team.Slug</c> is uniquely indexed, so the second
/// case would not merely see the first case's row, it could not write its own. The
/// dev-login assertion catches the opposite mistake — a database reset out from
/// under a live app — which is the exact 500 the TRUNCATE approach produced when
/// <c>DevPersonaSeeder</c> resolved a cached user id whose row had just been deleted.
/// </para>
/// </remarks>
public sealed class DatabaseIsolationTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>Deliberately fixed, not GUID-suffixed. Scoping to a self-seeded key is the convention this test exists to make unnecessary.</summary>
    private const string SharedSlug = "issue-983-isolation-marker";

    [HumansTheory(Timeout = 60_000)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EachTestGetsAnEmptyDatabaseAndACoherentApp(int caseNumber)
    {
        var ct = TestContext.Current.CancellationToken;

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

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

        // The app's Singleton caches were built against this database and no other,
        // so the persona seeder resolves ids that still exist.
        var login = await Client.GetAsync("/dev/login/volunteer", ct);
        login.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "the app's caches must stay coherent with the database it is bound to");
    }
}
