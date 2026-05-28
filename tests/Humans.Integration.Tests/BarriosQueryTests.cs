using System.Net;
using AwesomeAssertions;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests;

public class BarriosQueryTests(HumansWebApplicationFactory factory) : IntegrationTestBase(factory)
{
    [HumansFact(Timeout = 60000)]
    public async Task AuthenticatedBarriosPage_DoesNotSelectUsers_WhenUserInfoCacheIsWarm()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        using (var warmScope = Factory.Services.CreateScope())
        {
            var users = warmScope.ServiceProvider.GetRequiredService<IUserServiceRead>();
            await users.GetAllUserInfosAsync();
        }

        // Pay any one-time auth/culture/claims costs from sign-in before measuring
        // the page reload itself.
        var warmResponse = await Client.GetAsync("/");
        warmResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var queryStats = scope.ServiceProvider.GetRequiredService<QueryStatistics>();
        queryStats.Reset();

        var response = await Client.GetAsync("/Barrios");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var userSelects = queryStats.GetSnapshot()
            .Where(entry =>
                string.Equals(entry.Operation, "SELECT", StringComparison.Ordinal) &&
                string.Equals(entry.Table, "users", StringComparison.Ordinal))
            .Sum(entry => entry.Count);
        userSelects.Should().Be(0, "the public barrios directory should read the current human from UserInfo cache, not Identity's users table");
    }
}
