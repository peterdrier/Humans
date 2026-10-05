using Humans.Users.Contracts;

namespace Humans.Tickets.Tests;

/// <summary>
/// The <c>User</c> → <see cref="UserInfo"/> projection for the section's tests.
/// </summary>
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
