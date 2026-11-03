using System.Security.Claims;
using AwesomeAssertions;
using Humans.CityPlanning.Authorization;
using Humans.CityPlanning.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Humans.CityPlanning.Tests;

public sealed class CityPlanningMapAdminHandlerTests
{
    [HumansFact]
    public async Task AuthorizationRead_PropagatesAbandonedRequests()
    {
        using var request = new CancellationTokenSource();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } };
        var cityPlanning = Substitute.For<ICityPlanningService>();
        var userId = Guid.NewGuid();
        var observed = CancellationToken.None;
        cityPlanning.IsCityPlanningTeamMemberAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            request.Cancel();
            observed.ThrowIfCancellationRequested();
            return true;
        });
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        var context = new AuthorizationHandlerContext([new CityPlanningMapAdminRequirement()], principal, null);
        var handler = new CityPlanningMapAdminHandler(cityPlanning, http);
        Func<Task> authorize = () => handler.HandleAsync(context);
        await authorize.Should().ThrowAsync<OperationCanceledException>();
        observed.Should().Be(request.Token);
        context.HasSucceeded.Should().BeFalse();
    }
}
