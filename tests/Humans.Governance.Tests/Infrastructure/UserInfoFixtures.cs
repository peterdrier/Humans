using Humans.Users.Contracts;

namespace Humans.Governance.Tests.Infrastructure;

/// <summary>
/// Builds the minimal <see cref="UserInfo"/> Governance's service tests hand to their
/// <c>IUserService</c> substitutes.
/// </summary>
internal static class UserInfoFixtures
{
    public static UserInfo ToUserInfo(this User user, ProfileInfo? profile = null)
        => UserInfo.Create(
            user,
            user.UserEmails?.ToList() ?? [],
            [],
            [],
            profile: profile,
            []);
}
