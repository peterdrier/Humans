using AwesomeAssertions;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Services.Mailer.Audiences;
using Humans.Domain.Entities;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer.Audiences;

public class TicketNoShiftsAudienceTests
{
    private static readonly Guid EventId = Guid.NewGuid();

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ReturnsTicketHoldersMinusShiftHavers()
    {
        var userA = Guid.NewGuid(); // ticket, no shift          → IN
        var userB = Guid.NewGuid(); // ticket, has Confirmed shift → OUT
        var userC = Guid.NewGuid(); // no ticket                  → OUT (not in ticketHolders)
        var userD = Guid.NewGuid(); // ticket, has Pending shift   → OUT
        _ = userC;

        var audience = NewAudience(
            ticketHolders: [userA, userB, userD],
            shiftCommitted: [userB, userD]);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([userA]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoActiveEvent_ReturnsEmpty()
    {
        var audience = NewAudience(
            ticketHolders: [Guid.NewGuid()],
            shiftCommitted: [],
            activeEvent: null,
            useDefaultEvent: false);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEmpty();
    }

    [HumansFact]
    public void Metadata_UsesHumansPrefix()
    {
        var audience = NewAudience([], []);
        audience.Key.Should().Be("ticket-no-shifts");
        audience.MailerLiteGroupName.Should().Be("Humans - Ticket no Shifts");
        audience.MailerLiteGroupName.Should().StartWith("Humans - ");
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_TicketWithoutCommittedShift_IncludesUser()
    {
        // Users with only Refused/Bailed/Cancelled/NoShow signups are NOT in
        // shiftCommitted (per repo semantics — only Pending+Confirmed count).
        // They should remain in the audience.
        var userA = Guid.NewGuid();
        var audience = NewAudience(
            ticketHolders: [userA],
            shiftCommitted: []);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([userA]);
    }

    private static TicketNoShiftsAudience NewAudience(
        HashSet<Guid> ticketHolders,
        HashSet<Guid> shiftCommitted,
        EventSettings? activeEvent = null,
        bool useDefaultEvent = true)
    {
        var tickets = Substitute.For<ITicketQueryService>();
        tickets.GetUserIdsWithTicketsAsync().Returns(ticketHolders);

        var signups = Substitute.For<IShiftSignupService>();
        signups.GetActiveCommittedUserIdsForEventAsync(EventId, Arg.Any<CancellationToken>())
            .Returns(shiftCommitted);

        var mgmt = Substitute.For<IShiftManagementService>();
        var resolved = activeEvent ?? (useDefaultEvent ? FakeEventSettings(EventId) : null);
        mgmt.GetActiveAsync().Returns(resolved);

        return new TicketNoShiftsAudience(tickets, signups, mgmt);
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
