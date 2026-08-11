using Humans.Application;
using Humans.Domain.Entities;

namespace Humans.Governance.Tests.Infrastructure;

/// <summary>
/// Builds the minimal <see cref="UserInfo"/> Governance's service tests hand to their
/// <c>IUserService</c> substitutes.
/// </summary>
/// <remarks>
/// The pure half of <c>Humans.Application.Tests</c>' <c>UserInfoStubHelpers</c>. That file
/// also carries <c>StubGetUserInfosFromDb</c> overloads built on an in-memory
/// <c>HumansDbContext</c>, so linking it into the shared test set would push
/// <c>InternalsVisibleTo</c> on <c>HumansDbContext</c> — and a <c>Humans.Infrastructure</c>
/// dependency — into every section test project, which is the boundary G5 exists to draw
/// (nobodies-collective/Humans#866). Governance's tests stub the user reads themselves and
/// need only the projection.
/// </remarks>
internal static class UserInfoFixtures
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
