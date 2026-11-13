using Humans.Users.Contracts;

namespace Humans.Testing;

/// <summary>
/// Internal because <see cref="UserEmail"/> is internal to Base; the section test projects that use it are named in
/// this project's <c>InternalsVisibleTo</c> list.
/// </summary>
internal static class UserInfoFixtures
{
    /// <summary>
    /// The one test-side <see cref="User"/> to <see cref="UserInfo"/> projection: the user's emails (or
    /// <paramref name="userEmails"/>), no external logins. Section tests share it instead of keeping copies.
    /// </summary>
    public static UserInfo ToUserInfo(
        this User user,
        IReadOnlyList<UserEmail>? userEmails = null,
        ProfileInfo? profile = null) =>
        UserInfo.Create(user, userEmails ?? user.UserEmails?.ToList() ?? [], [], profile);
}
