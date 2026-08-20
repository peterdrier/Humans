using Humans.Users.Contracts;

namespace Humans.AuditLog.Tests.Infrastructure;

/// <summary>
/// Builds a minimal <see cref="UserInfo"/> for stubbing <c>IUserServiceRead</c> in this
/// project's tests. Every section's test project carries its own copy of this factory
/// (Teams, Shifts, Users, MailerLite, Camps …); this one is trimmed to the single member
/// <c>AuditViewerServiceTests</c> uses, so it needs no <c>Humans.Users</c> reference.
/// </summary>
internal static class UserInfoStubHelpers
{
    public static UserInfo MakeUserInfo(Guid userId, ProfileInfo? profile = null, string displayName = "User")
        => UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en" },
            [],
            [],
            [],
            profile: profile ?? UserFixtures.Profile(
                burnerName: displayName,
                state: ProfileState.Active,
                isApproved: true),
            []);
}
