using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.Services;

public sealed class UsersNotificationMetersTests : IDisposable
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ServiceProvider _localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
    private readonly UsersNotificationMeters _sut;

    public UsersNotificationMetersTests() =>
        _sut = new UsersNotificationMeters(_users, _localization.GetRequiredService<IStringLocalizer<UsersResource>>());

    public void Dispose() => _localization.Dispose();

    [HumansTheory]
    [InlineData(RoleNames.Admin, 1)]
    [InlineData(RoleNames.Board, 0)]
    public async Task OnlyAdmins_SeePendingAccountDeletions(string role, int expectedMeters)
    {
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            UserInfoFixtures.FromUser(new User { Id = Guid.NewGuid(), DeletionRequestedAt = Instant.FromUtc(2026, 4, 1, 0, 0) }),
            UserInfoFixtures.FromUser(new User { Id = Guid.NewGuid(), DeletionRequestedAt = Instant.FromUtc(2026, 4, 2, 0, 0) }),
            UserInfoFixtures.FromUser(new User { Id = Guid.NewGuid() }),
        });
        var viewer = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

        var meters = await _sut.GetMetersAsync(viewer, Guid.NewGuid(), TestContext.Current.CancellationToken);

        meters.Should().HaveCount(expectedMeters);
        if (expectedMeters == 1)
        {
            meters[0].Title.Should().Be("Pending account deletions");
            meters[0].Count.Should().Be(2);
            meters[0].ActionUrl.Should().Be("/Users/Admin?filter=deleting&sort=name&dir=asc");
            meters[0].Audience.Should().Be("Admins");
            meters[0].Priority.Should().Be(8);
        }
    }
}
