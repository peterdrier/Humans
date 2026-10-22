using Humans.Users.Contracts;

namespace Humans.Tickets.Tests;

internal static class UserInfoProjection
{
    public static UserInfo ToUserInfo(
        this User user,
        IReadOnlyList<UserEmail>? userEmails = null,
        ProfileInfo? profile = null)
        => UserInfo.Create(
            user,
            userEmails ?? user.UserEmails?.ToList() ?? [],
            [],
            [],
            profile: profile,
            []);
}
