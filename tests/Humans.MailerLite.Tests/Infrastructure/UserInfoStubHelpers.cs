using Humans.Users.Contracts;

namespace Humans.MailerLite.Tests.Infrastructure;

/// <summary>
/// The two pure <see cref="UserInfo"/> projections the audience tests use. Copied rather than
/// shared: the shared helper also builds on <c>UsersDbContext</c>, which would push
/// <c>InternalsVisibleTo</c> on it into every section test project.
/// </summary>
internal static class UserInfoStubHelpers
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

    public static UserInfo MakeUserInfo(Guid userId, ProfileInfo? profile = null, string displayName = "User")
    {
        var resolvedProfile = profile ?? UserFixtures.Profile(
            burnerName: displayName,
            isApproved: true);
        return UserInfo.Create(
            // UserInfo.BurnerName reads User.BurnerName, so it is seeded there.
            new User { Id = userId, PreferredLanguage = "en", BurnerName = resolvedProfile.BurnerName },
            [],
            [],
            [],
            profile: resolvedProfile,
            []);
    }
}
