using Humans.Users.Contracts;

namespace Humans.Onboarding.Tests.Services;

/// <summary>
/// The two pure <c>UserInfo</c> projections these tests use, copied out of
/// <c>UserInfoStubHelpers</c> (now <c>tests/Humans.AuditLog.Tests/Infrastructure/</c>).
/// </summary>
/// <remarks>
/// Copied rather than shared through <c>tests/Directory.Build.props</c> because the same
/// file also carries <c>StubGetUserInfosFromDb</c> overloads built on an in-memory
/// <c>UsersDbContext</c>: sharing it would push <c>InternalsVisibleTo</c> on
/// <c>UsersDbContext</c> into every section test project (design §15 step 8,
/// Governance's "split the helper before deciding").
/// </remarks>
internal static class UserInfoStubs
{
    internal static UserInfo ToUserInfo(
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

    internal static UserInfo MakeUserInfo(Guid userId, ProfileInfo? profile = null, string displayName = "User")
    {
        var info = profile ?? UserFixtures.Profile(burnerName: displayName, isApproved: true);
        return UserInfo.Create(
            // BurnerName mirrors CopyNamesToUser's dual-write from Profile onto User (#1097) —
            // UserInfo.BurnerName reads User.BurnerName only (#1098).
            new User { Id = userId, PreferredLanguage = "en", BurnerName = info.BurnerName, State = UserFixtures.StateFor(info) },
            [],
            [],
            [],
            profile: info,
            []);
    }
}
