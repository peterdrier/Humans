using System.Security.Claims;
using AwesomeAssertions;
using Humans.CityPlanning.Authorization;
using Humans.CityPlanning.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Humans.Users.Contracts;
using Xunit;

namespace Humans.CityPlanning.Tests;

public sealed class CityPlanningMapAdminHandlerTests
{
    [HumansTheory]
    [InlineData("User")]
    [InlineData("Team")]
    public async Task AuthorizationRead_PropagatesAbandonedRequests(string boundary)
    {
        using var request = new CancellationTokenSource();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } };
        var cityPlanning = Substitute.For<ICityPlanningService>();
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        var observed = CancellationToken.None;
        void Read(string name, CancellationToken ct)
        {
            if (string.Equals(name, boundary, StringComparison.Ordinal))
            {
                observed = ct;
                request.Cancel();
            }
            ct.ThrowIfCancellationRequested();
        }
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("User", call.Arg<CancellationToken>());
            return UserInfo.Create(new User { Id = userId }, [], [], profile: null);
        });
        cityPlanning.IsCityPlanningTeamMemberAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Read("Team", call.Arg<CancellationToken>());
            return true;
        });
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        var context = new AuthorizationHandlerContext([new CityPlanningMapAdminRequirement()], principal, null);
        var handler = new CityPlanningMapAdminHandler(cityPlanning, users, http);
        Func<Task> authorize = () => handler.HandleAsync(context);
        await authorize.Should().ThrowAsync<OperationCanceledException>();
        observed.Should().Be(request.Token);
        context.HasSucceeded.Should().BeFalse();
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TeamAccess_UsesResolvedAccountAndDeniesMissingAccounts(bool exists)
    {
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>())
            .Returns(exists ? UserInfo.Create(new User { Id = survivorId }, [], [], profile: null) : null);
        var cityPlanning = Substitute.For<ICityPlanningService>();
        cityPlanning.IsCityPlanningTeamMemberAsync(exists ? survivorId : cookieId, Arg.Any<CancellationToken>())
            .Returns(true);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cookieId.ToString())], "test"));
        var context = new AuthorizationHandlerContext([new CityPlanningMapAdminRequirement()], principal, null);
        var handler = new CityPlanningMapAdminHandler(cityPlanning, users, new HttpContextAccessor());

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().Be(exists);
        if (!exists) cityPlanning.ReceivedCalls().Should().BeEmpty();
    }

}
