using Humans.Users.Contracts;

namespace Humans.Tickets.Tests;

/// <summary>
/// The <c>User</c> → <see cref="UserInfo"/> projection out of
/// <c>Humans.Application.Tests</c>' <c>UserInfoStubHelpers</c>.
/// </summary>
/// <remarks>
/// Copied, not shared. The rest of that file stubs the readers off an in-memory
/// <c>HumansDbContext</c>, so linking it into the shared test set would push
/// <c>InternalsVisibleTo</c> on that context into every section test project — Governance's
/// "split the helper before deciding", which for this file lands on "inline the twelve lines"
/// (G5-SECTION-TEMPLATE.md step 8).
/// </remarks>
internal static class UserInfoProjection
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
}
