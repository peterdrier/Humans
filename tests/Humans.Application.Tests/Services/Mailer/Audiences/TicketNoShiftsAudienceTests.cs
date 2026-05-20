using AwesomeAssertions;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Mailer.Audiences;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer.Audiences;

public class TicketNoShiftsAudienceTests
{
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ReturnsTicketHoldersMinusShiftHavers()
    {
        var userA = Guid.NewGuid(); // ticket, no shift             → IN
        var userB = Guid.NewGuid(); // ticket, has Confirmed shift   → OUT
        var userC = Guid.NewGuid(); // no ticket                     → OUT (not in ticketHolders)
        var userD = Guid.NewGuid(); // ticket, has Pending shift     → OUT
        _ = userC;

        var audience = NewAudience(
            ticketHolders: [userA, userB, userD],
            shiftCommitted: [userB, userD]);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([userA]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoTicketHolders_ReturnsEmpty()
    {
        var audience = NewAudience(
            ticketHolders: [],
            shiftCommitted: []);

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
        // shiftCommitted (per ShiftUserView.HasShift — only Pending+Confirmed count).
        // They should remain in the audience.
        var userA = Guid.NewGuid();
        var audience = NewAudience(
            ticketHolders: [userA],
            shiftCommitted: []);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([userA]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_DoesNotInjectShiftSignupOrManagementService()
    {
        // Constructor surface check: the audience must no longer depend on
        // IShiftSignupService or IShiftManagementService. If a future change
        // reintroduces either, the DI registration in MailerSectionExtensions
        // would need to wire them — and this test will fail at the type level.
        var ctor = typeof(TicketNoShiftsAudience).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();
        paramTypes.Should().NotContain(typeof(IShiftSignupService));
        paramTypes.Should().NotContain(typeof(IShiftManagementService));
        paramTypes.Should().Contain(typeof(IShiftView));
    }

    private static TicketNoShiftsAudience NewAudience(
        HashSet<Guid> ticketHolders,
        HashSet<Guid> shiftCommitted)
    {
        var tickets = Substitute.For<ITicketQueryService>();
        tickets.GetUserIdsWithTicketsAsync().Returns(ticketHolders);

        var shiftView = Substitute.For<IShiftView>();
        shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = ((IEnumerable<Guid>)callInfo[0]).ToList();
                var map = new Dictionary<Guid, ShiftUserView>();
                foreach (var id in ids)
                {
                    map[id] = shiftCommitted.Contains(id)
                        ? ViewWithShift(id)
                        : ShiftUserView.Empty(id);
                }
                return new ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>>(map);
            });

        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new List<UserInfo>());

        return new TicketNoShiftsAudience(tickets, shiftView, users);
    }

    private static ShiftUserView ViewWithShift(Guid userId) => new(
        userId,
        Profile: null,
        Availability: null,
        BuildStatus: null,
        TagPreferences: [],
        Signups: [new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = Guid.NewGuid(),
            Status = SignupStatus.Confirmed,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        }]);
}
