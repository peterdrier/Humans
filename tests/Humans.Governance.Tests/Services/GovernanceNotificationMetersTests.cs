using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Governance.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Governance.Tests.Services;

public sealed class GovernanceNotificationMetersTests : IDisposable
{
    private readonly IGovernanceManagement _applications = Substitute.For<IGovernanceManagement>();
    private readonly ServiceProvider _localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
    private readonly GovernanceNotificationMeters _sut;

    public GovernanceNotificationMetersTests() =>
        _sut = new GovernanceNotificationMeters(
            _applications, _localization.GetRequiredService<IStringLocalizer<GovernanceResource>>());

    public void Dispose() => _localization.Dispose();

    [HumansTheory]
    [InlineData(RoleNames.Board, 3, 1)]
    [InlineData(RoleNames.Board, 0, 0)]
    [InlineData(RoleNames.Admin, 3, 0)]
    public async Task GetMetersAsync_ShowsTheBoardMemberTheirOwnUnvotedCount(string role, int unvoted, int expectedMeters)
    {
        var userId = Guid.NewGuid();
        _applications.GetUnvotedApplicationCountAsync(userId, Arg.Any<CancellationToken>()).Returns(unvoted);
        var viewer = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

        var meters = await _sut.GetMetersAsync(viewer, userId, TestContext.Current.CancellationToken);

        meters.Should().HaveCount(expectedMeters);
        if (expectedMeters == 1)
        {
            meters[0].Count.Should().Be(unvoted);
            meters[0].Title.Should().Be("Applications pending your vote");
            meters[0].Audience.Should().Be("the Board");
            meters[0].ActionUrl.Should().Be("/Governance/BoardVoting");
        }
    }
}
