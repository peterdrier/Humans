using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Google.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Google.Tests;

public sealed class GoogleNotificationMetersTests : IDisposable
{
    private readonly IGoogleSyncManagement _sync = Substitute.For<IGoogleSyncManagement>();
    private readonly ServiceProvider _localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
    private readonly GoogleNotificationMeters _sut;

    public GoogleNotificationMetersTests() =>
        _sut = new GoogleNotificationMeters(_sync, _localization.GetRequiredService<IStringLocalizer<GoogleResource>>());

    public void Dispose() => _localization.Dispose();

    [HumansTheory]
    [InlineData(5, 1)]
    [InlineData(0, 0)]
    public async Task Admin_SeesFailedSyncEventsWhenAny(int failed, int expectedMeters)
    {
        _sync.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(failed);

        var meters = await _sut.GetMetersAsync(Viewer(RoleNames.Admin), Guid.NewGuid(), TestContext.Current.CancellationToken);

        meters.Should().HaveCount(expectedMeters);
        if (expectedMeters == 1)
        {
            meters[0].Title.Should().Be("Failed Google sync events");
            meters[0].Count.Should().Be(failed);
            meters[0].ActionUrl.Should().Be("/Google/SyncOutbox");
            meters[0].Audience.Should().Be("Admins");
            meters[0].Priority.Should().Be(7);
        }
    }

    [HumansFact]
    public async Task NonAdmin_GetsNoMeterAndNoQuery()
    {
        var meters = await _sut.GetMetersAsync(Viewer(RoleNames.Board), Guid.NewGuid(), TestContext.Current.CancellationToken);

        meters.Should().BeEmpty();
        await _sync.DidNotReceiveWithAnyArgs().GetFailedSyncEventCountAsync();
    }

    private static ClaimsPrincipal Viewer(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
}
