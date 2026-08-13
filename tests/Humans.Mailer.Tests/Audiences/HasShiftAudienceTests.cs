using Humans.Application;
using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Mailer.Services.Audiences;
using Humans.Mailer.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NSubstitute;

namespace Humans.Mailer.Tests.Audiences;

public class HasShiftAudienceTests
{
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ReturnsUsersWithHasShiftTrue()
    {
        var userA = Guid.NewGuid(); // Confirmed   → IN
        var userB = Guid.NewGuid(); // Pending     → IN
        var userC = Guid.NewGuid(); // Cancelled   → OUT
        var userD = Guid.NewGuid(); // no signups  → OUT

        var audience = NewAudience(new Dictionary<Guid, ShiftUserSummary>
        {
            [userA] = ViewWith(userA, SignupStatus.Confirmed),
            [userB] = ViewWith(userB, SignupStatus.Pending),
            [userC] = ViewWith(userC, SignupStatus.Cancelled),
            [userD] = ShiftUserSummary.Empty(userD),
        });

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEquivalentTo([userA, userB]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoUsers_ReturnsEmpty()
    {
        var audience = NewAudience(new Dictionary<Guid, ShiftUserSummary>());

        var members = await audience.ComputeMemberUserIdsAsync(Xunit.TestContext.Current.CancellationToken);

        members.Should().BeEmpty();
    }

    [HumansFact]
    public void Metadata_UsesHumansPrefix()
    {
        var audience = NewAudience(new Dictionary<Guid, ShiftUserSummary>());
        audience.Key.Should().Be("has-shift");
        audience.MailerLiteGroupName.Should().Be("Humans - Has Shift");
        audience.MailerLiteGroupName.Should().StartWith("Humans - ");
    }

    private static HasShiftAudience NewAudience(IReadOnlyDictionary<Guid, ShiftUserSummary> viewsByUser)
    {
        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(viewsByUser.Keys
                .Select(id => new User { Id = id }.ToUserInfo())
                .ToList());

        var shiftView = Substitute.For<IShiftView>();
        shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, ShiftUserSummary>>(viewsByUser));

        return new HasShiftAudience(shiftView, users);
    }

    private static ShiftUserSummary ViewWith(Guid userId, SignupStatus status) =>
        ShiftFixtures.UserSummary(userId, [ShiftFixtures.Signup(status: status)]);
}
