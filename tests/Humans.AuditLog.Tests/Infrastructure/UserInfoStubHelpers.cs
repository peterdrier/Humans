using Humans.Users.Contracts;

namespace Humans.AuditLog.Tests.Infrastructure;

/// <summary>
/// Trimmed to the single member <c>AuditViewerServiceTests</c> uses, so this project needs no
/// <c>Humans.Users</c> reference.
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
                isApproved: true),
            []);
}
