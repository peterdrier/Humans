using Humans.Users.Contracts;

namespace Humans.Onboarding.Tests.Services;

/// <summary>
/// The two pure <c>UserInfo</c> projections these tests use. Copied rather than shared: the shared
/// helper also builds on <c>UsersDbContext</c>, which would push <c>InternalsVisibleTo</c> on it
/// into every section test project.
/// </summary>
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
            // UserInfo.BurnerName reads User.BurnerName, so it is seeded there.
            new User { Id = userId, PreferredLanguage = "en", BurnerName = info.BurnerName, State = UserFixtures.StateFor(info) },
            [],
            [],
            [],
            profile: info,
            []);
    }
}
