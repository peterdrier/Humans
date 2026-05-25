using AwesomeAssertions;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Mailer.Audiences;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer.Audiences;

public class HasShiftAudienceTests
{
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ReturnsUsersWithHasShiftTrue()
    {
        var userA = Guid.NewGuid(); // Confirmed   → IN
        var userB = Guid.NewGuid(); // Pending     → IN
        var userC = Guid.NewGuid(); // Cancelled   → OUT
        var userD = Guid.NewGuid(); // no signups  → OUT

        var audience = NewAudience(new Dictionary<Guid, ShiftUserView>
        {
            [userA] = ViewWith(userA, SignupStatus.Confirmed),
            [userB] = ViewWith(userB, SignupStatus.Pending),
            [userC] = ViewWith(userC, SignupStatus.Cancelled),
            [userD] = ShiftUserView.Empty(userD),
        });

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([userA, userB]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoUsers_ReturnsEmpty()
    {
        var audience = NewAudience(new Dictionary<Guid, ShiftUserView>());

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEmpty();
    }

    [HumansFact]
    public void Metadata_UsesHumansPrefix()
    {
        var audience = NewAudience(new Dictionary<Guid, ShiftUserView>());
        audience.Key.Should().Be("has-shift");
        audience.MailerLiteGroupName.Should().Be("Humans - Has Shift");
        audience.MailerLiteGroupName.Should().StartWith("Humans - ");
    }

    private static HasShiftAudience NewAudience(IReadOnlyDictionary<Guid, ShiftUserView> viewsByUser)
    {
        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(viewsByUser.Keys
                .Select(id => new User { Id = id }.ToUserInfo())
                .ToList());

        var shiftView = Substitute.For<IShiftView>();
        shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>>(viewsByUser));

        return new HasShiftAudience(shiftView, users);
    }

    private static ShiftUserView ViewWith(Guid userId, SignupStatus status) => new(
        UserId: userId,
        Profile: null,
        Availability: null,
        BuildStatus: null,
        TagPreferences: [],
        Signups: [new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = Guid.NewGuid(),
            Status = status,
        }]);
}
