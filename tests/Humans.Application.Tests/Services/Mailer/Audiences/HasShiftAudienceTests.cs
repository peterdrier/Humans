using AwesomeAssertions;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Services.Mailer.Audiences;
using Humans.Domain.Entities;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer.Audiences;

public class HasShiftAudienceTests
{
    private static readonly Guid EventId = Guid.NewGuid();

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ReturnsCommittedShiftUsers()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var audience = NewAudience(shiftCommitted: [userA, userB]);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([userA, userB]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoActiveEvent_ReturnsEmpty()
    {
        var audience = NewAudience(
            shiftCommitted: [Guid.NewGuid()],
            activeEvent: null,
            useDefaultEvent: false);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEmpty();
    }

    [HumansFact]
    public void Metadata_UsesHumansPrefix()
    {
        var audience = NewAudience([]);
        audience.Key.Should().Be("has-shift");
        audience.MailerLiteGroupName.Should().Be("Humans - Has Shift");
        audience.MailerLiteGroupName.Should().StartWith("Humans - ");
    }

    private static HasShiftAudience NewAudience(
        HashSet<Guid> shiftCommitted,
        EventSettings? activeEvent = null,
        bool useDefaultEvent = true)
    {
        var signups = Substitute.For<IShiftSignupService>();
        signups.GetActiveCommittedUserIdsForEventAsync(EventId, Arg.Any<CancellationToken>())
            .Returns(shiftCommitted);

        var mgmt = Substitute.For<IShiftManagementService>();
        var resolved = activeEvent ?? (useDefaultEvent ? FakeEventSettings(EventId) : null);
        mgmt.GetActiveAsync().Returns(resolved);

        return new HasShiftAudience(signups, mgmt);
    }

    private static EventSettings FakeEventSettings(Guid id)
    {
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);
        return new EventSettings
        {
            Id = id,
            EventName = "Test Event",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsShiftBrowsingOpen = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
