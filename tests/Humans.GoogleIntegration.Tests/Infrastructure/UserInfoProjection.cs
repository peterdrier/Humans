using Humans.Application;
using Humans.Domain.Entities;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>
/// The one pure member of <c>Humans.Application.Tests</c>' <c>UserInfoStubHelpers</c> that the
/// section's tests use: a <see cref="User"/> to <see cref="UserInfo"/> projection with empty
/// collections for everything the section never reads.
/// </summary>
/// <remarks>
/// Copied rather than shared. The original file's other members stub
/// <c>IUserService.GetUserInfosAsync</c> over an in-memory <c>UsersDbContext</c>, so sharing it
/// would push <c>InternalsVisibleTo</c> on that context into this project for a twelve-line
/// projection (Governance's split-the-helper rule, G5-SECTION-TEMPLATE.md step 8).
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
