using Humans.Application;
using Humans.Domain.Enums;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Mailer.Tests.Infrastructure;

/// <summary>
/// The two pure <see cref="UserInfo"/> projections the audience tests use, copied out of
/// <c>Humans.Application.Tests.Infrastructure.UserInfoStubHelpers</c> at Mailer's G5 move
/// (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// Copied rather than shared through <c>tests/Directory.Build.props</c>: the original file
/// also carries <c>StubGetUserInfosFromDb</c> overloads built on an in-memory
/// <c>UsersDbContext</c>, so linking it in would push an EF dependency — and
/// <c>InternalsVisibleTo</c> on <c>UsersDbContext</c> — into a section test project that
/// has neither (Governance's rule: split the helper before deciding).
/// </remarks>
internal static class UserInfoStubHelpers
{
    public static UserInfo ToUserInfo(
        this User user,
        IReadOnlyList<UserEmail>? userEmails = null,
        Profile? profile = null)
        => UserInfo.Create(
            user,
            userEmails ?? user.UserEmails?.ToList() ?? [],
            [],
            [],
            profile: profile,
            [],
            [],
            [],
            []);

    public static UserInfo MakeUserInfo(Guid userId, Profile? profile = null, string displayName = "User")
        => UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en" },
            [],
            [],
            [],
            profile: profile ?? new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BurnerName = displayName,
                CreatedAt = SystemClock.Instance.GetCurrentInstant(),
                UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
                State = ProfileState.Active,
                IsApproved = true
            },
            [],
            [],
            [],
            []);
}
