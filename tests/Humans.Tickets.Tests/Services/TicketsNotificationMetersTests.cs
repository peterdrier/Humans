using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Tickets.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Services;

public sealed class TicketsNotificationMetersTests : IDisposable
{
    private readonly ITicketSyncService _sync = Substitute.For<ITicketSyncService>();
    private readonly ServiceProvider _localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
    private readonly TicketsNotificationMeters _sut;

    public TicketsNotificationMetersTests() =>
        _sut = new TicketsNotificationMeters(_sync, _localization.GetRequiredService<IStringLocalizer<TicketsResource>>());

    public void Dispose() => _localization.Dispose();

    [HumansTheory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Admin_SeesTheSyncErrorWhileSyncIsInError(bool inError, int expectedMeters)
    {
        _sync.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(inError);

        var meters = await _sut.GetMetersAsync(Viewer(RoleNames.Admin), Guid.NewGuid(), TestContext.Current.CancellationToken);

        meters.Should().HaveCount(expectedMeters);
        if (expectedMeters == 1)
        {
            meters[0].Title.Should().Be("Ticket sync error");
            meters[0].Count.Should().Be(1);
            meters[0].ActionUrl.Should().Be("/Tickets");
            meters[0].Audience.Should().Be("Admins");
            meters[0].Priority.Should().Be(4);
        }
    }

    [HumansFact]
    public async Task NonAdmin_GetsNoMeterAndNoRead()
    {
        var meters = await _sut.GetMetersAsync(Viewer(RoleNames.Board), Guid.NewGuid(), TestContext.Current.CancellationToken);

        meters.Should().BeEmpty();
        await _sync.DidNotReceiveWithAnyArgs().IsInErrorStateAsync();
    }

    private static ClaimsPrincipal Viewer(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
}
