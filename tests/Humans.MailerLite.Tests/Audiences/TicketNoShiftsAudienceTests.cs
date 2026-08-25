using AwesomeAssertions;
using Humans.MailerLite.Services.Audiences;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Testing;
using Xunit;

namespace Humans.MailerLite.Tests.Audiences;

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

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEquivalentTo([userA]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoTicketHolders_ReturnsEmpty()
    {
        var audience = NewAudience(
            ticketHolders: [],
            shiftCommitted: []);

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

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

    // A ticket holder whose only signups were Refused/Bailed/Cancelled/NoShow has not
    // committed to a shift and must stay in the audience. This constructs those signups
    // rather than handing back an empty summary — an empty summary would walk the same
    // path as ComputeMemberUserIdsAsync_ReturnsTicketHoldersMinusShiftHavers and prove
    // nothing about the statuses named here.
    [HumansTheory]
    [InlineData(SignupStatus.Refused)]
    [InlineData(SignupStatus.Bailed)]
    [InlineData(SignupStatus.Cancelled)]
    [InlineData(SignupStatus.NoShow)]
    public async Task ComputeMemberUserIdsAsync_TicketWithOnlyUncommittedSignups_IncludesUser(
        SignupStatus status)
    {
        var userA = Guid.NewGuid();
        var summary = ShiftFixtures.UserSummary(userA, [ShiftFixtures.Signup(status: status)]);
        summary.HasShift.Should().BeFalse(
            $"{status} must not count as a committed shift — only Pending and Confirmed do");

        var audience = NewAudience(
            ticketHolders: [userA],
            shiftCommitted: [],
            summaryOverrides: new Dictionary<Guid, ShiftUserSummary> { [userA] = summary });

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEquivalentTo([userA]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_DoesNotInjectShiftSignupOrManagementService()
    {
        // Constructor surface check: the audience must no longer depend on
        // IShiftSignups or IShiftManagementServiceRead. If a future change
        // reintroduces either, the DI registration in MailerLiteSectionExtensions
        // would need to wire them — and this test will fail at the type level.
        var ctor = typeof(TicketNoShiftsAudience).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();
        paramTypes.Should().NotContain(typeof(IShiftSignups));
        paramTypes.Should().NotContain(typeof(IShiftManagementServiceRead));
        paramTypes.Should().Contain(typeof(IShiftView));
    }

    private static TicketNoShiftsAudience NewAudience(
        HashSet<Guid> ticketHolders,
        HashSet<Guid> shiftCommitted,
        IReadOnlyDictionary<Guid, ShiftUserSummary>? summaryOverrides = null)
    {
        var tickets = Substitute.For<ITicketServiceRead>();
        tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(OrdersForTicketHolders(ticketHolders));

        var shiftView = Substitute.For<IShiftView>();
        shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = ((IEnumerable<Guid>)callInfo[0]).ToList();
                var map = new Dictionary<Guid, ShiftUserSummary>();
                foreach (var id in ids)
                {
                    map[id] = summaryOverrides is not null && summaryOverrides.TryGetValue(id, out var over)
                        ? over
                        : shiftCommitted.Contains(id)
                            ? ViewWithShift(id)
                            : ShiftUserSummary.Empty(id);
                }
                return new ValueTask<IReadOnlyDictionary<Guid, ShiftUserSummary>>(map);
            });

        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new List<UserInfo>());

        return new TicketNoShiftsAudience(tickets, shiftView, users);
    }

    private static IReadOnlyList<TicketOrderInfo> OrdersForTicketHolders(IEnumerable<Guid> userIds) =>
        userIds.Select(userId => new TicketOrderInfo(
            Id: Guid.NewGuid(),
            VendorOrderId: $"ord_{userId:N}",
            BuyerName: null,
            BuyerEmail: null,
            TotalAmount: 0m,
            Currency: "EUR",
            DiscountCode: null,
            PaymentStatus: TicketPaymentStatus.Paid,
            VendorEventId: "ev_test",
            PurchasedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            MatchedUserId: null,
            IsCurrentEvent: true,
            Attendees: [new TicketAttendeeInfo(
                Id: Guid.NewGuid(),
                VendorTicketId: $"tkt_{userId:N}",
                AttendeeName: null,
                AttendeeEmail: null,
                TicketTypeName: null,
                Price: 0m,
                Status: TicketAttendeeStatus.Valid,
                MatchedUserId: userId)])).ToList();

    private static ShiftUserSummary ViewWithShift(Guid userId) =>
        ShiftFixtures.UserSummary(
            userId,
            [ShiftFixtures.Signup(status: SignupStatus.Confirmed)]);
}
