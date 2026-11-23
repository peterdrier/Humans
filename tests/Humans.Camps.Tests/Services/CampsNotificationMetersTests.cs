using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Camps.Tests.Services;

public sealed class CampsNotificationMetersTests : IDisposable
{
    private static readonly Guid CurrentEventId = Guid.NewGuid();
    private readonly ICampService _camps = Substitute.For<ICampService>();
    private readonly ServiceProvider _localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
    private readonly CampsNotificationMeters _sut;
    private static readonly ClaimsPrincipal Viewer = new(new ClaimsIdentity());

    public CampsNotificationMetersTests()
    {
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, [2026])
        {
            ActiveEventSettingsId = CurrentEventId,
            Events = [new(CurrentEventId, "Current event", 2026, true)]
        });
        _sut = new CampsNotificationMeters(_camps, _localization.GetRequiredService<IStringLocalizer<CampsResource>>());
    }

    public void Dispose() => _localization.Dispose();

    [HumansFact]
    public async Task Lead_SeesTheirPendingRequests_SingularThenPluralReadLive()
    {
        var leadId = Guid.NewGuid();
        _camps.GetCampsForEventAsync(CurrentEventId, Arg.Any<CancellationToken>())
            .Returns([CampWithPendingRequest(leadId)], [CampWithPendingRequest(leadId), CampWithPendingRequest(leadId)]);
        var ct = TestContext.Current.CancellationToken;

        var first = await _sut.GetMetersAsync(Viewer, leadId, ct);
        var second = await _sut.GetMetersAsync(Viewer, leadId, ct);

        var meter = first.Should().ContainSingle().Subject;
        meter.Title.Should().Be("1 human wants to join your camp");
        meter.Count.Should().Be(1);
        meter.ActionUrl.Should().Be("/Barrios");
        meter.Audience.Should().Be("Barrio leads");
        meter.Priority.Should().Be(3);
        second.Should().ContainSingle().Which.Title.Should().Be("2 humans want to join your camp");
    }

    [HumansFact]
    public async Task NotALead_GetsNoMeter()
    {
        _camps.GetCampsForEventAsync(CurrentEventId, Arg.Any<CancellationToken>())
            .Returns([CampWithPendingRequest(Guid.NewGuid())]);

        var meters = await _sut.GetMetersAsync(Viewer, Guid.NewGuid(), TestContext.Current.CancellationToken);

        meters.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountsOnlyActiveAndOpenEvents_OncePerEvent(bool activeRegistrationOpen)
    {
        var leadId = Guid.NewGuid();
        var otherOpenId = Guid.NewGuid();
        var closedId = Guid.NewGuid();
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, [2026])
        {
            ActiveEventSettingsId = CurrentEventId,
            Events = [new(CurrentEventId, "Active", 2026, activeRegistrationOpen),
                new(otherOpenId, "Open", 2026, true), new(closedId, "Closed", 2026, false)]
        });
        _camps.GetCampsForEventAsync(CurrentEventId, Arg.Any<CancellationToken>()).Returns([CampWithPendingRequest(leadId)]);
        _camps.GetCampsForEventAsync(otherOpenId, Arg.Any<CancellationToken>())
            .Returns([CampWithPendingRequest(leadId, otherOpenId)]);
        var ct = TestContext.Current.CancellationToken;

        var meters = await _sut.GetMetersAsync(Viewer, leadId, ct);

        meters.Should().ContainSingle().Which.Count.Should().Be(2);
        await _camps.Received(1).GetCampsForEventAsync(CurrentEventId, ct);
        await _camps.Received(1).GetCampsForEventAsync(otherOpenId, ct);
        await _camps.DidNotReceive().GetCampsForEventAsync(closedId, Arg.Any<CancellationToken>());
    }

    private static CampInfo CampWithPendingRequest(Guid leadUserId, Guid? eventSettingsId = null)
    {
        var campId = Guid.NewGuid();
        var pendingMember = new CampSeasonMemberInfo(
            Guid.NewGuid(), Guid.NewGuid(), CampMemberStatus.Pending,
            Instant.FromUtc(2026, 3, 1, 0, 0), ConfirmedAt: null, HasEarlyEntry: false);

        return new CampInfo(
            campId, "lead-camp", "lead@example.com", "+34600000000", IsSwissCamp: false, TimesAtNowhere: 1,
            Seasons:
            [
                new CampSeasonInfo(
                    Guid.NewGuid(), campId, "lead-camp", 2026, null, "Lead Camp",
                    string.Empty, string.Empty, [], CampSeasonStatus.Active,
                    YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
                    0, null, null, null, 0, null, null)
                {
                    EventSettingsId = eventSettingsId ?? CurrentEventId,
                    LeadUserIds = [leadUserId],
                    Members = [pendingMember]
                }
            ]);
    }
}
