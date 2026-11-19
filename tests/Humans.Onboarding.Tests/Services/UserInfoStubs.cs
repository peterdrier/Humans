using Humans.Users.Contracts;

namespace Humans.Onboarding.Tests.Services;

/// <summary>
/// The pure <c>UserInfo</c> projection these tests use. Copied rather than shared: the shared
/// helper also builds on <c>UsersDbContext</c>, which would push <c>InternalsVisibleTo</c> on it
/// into every section test project.
/// </summary>
internal static class UserInfoStubs
{
    internal static UserInfo MakeUserInfo(Guid userId, ProfileInfo? profile = null, string displayName = "User",
        string? burnerName = null, string firstName = "", string lastName = "")
    {
        var info = profile ?? UserFixtures.Profile();
        burnerName ??= displayName;
        return UserInfo.Create(
            // UserInfo.BurnerName reads User.BurnerName, so it is seeded there.
            new User { Id = userId, PreferredLanguage = "en", BurnerName = burnerName, FirstName = firstName, LastName = lastName, State = UserFixtures.StateFor(info, burnerName, firstName, lastName) },
            [],
            [],
            profile: info);
    }
}
