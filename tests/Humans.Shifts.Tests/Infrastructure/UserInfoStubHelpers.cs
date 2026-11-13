using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Domain;
using Humans.Users.Services;

namespace Humans.Shifts.Tests.Infrastructure;

/// <summary>
/// Stubs <see cref="IUserService.GetUserInfosAsync"/> from the test's in-memory DB: the User and
/// its UserEmails, empty collections for the rest.
/// </summary>
internal static class UserInfoStubHelpers
{
    /// <summary>
    /// <see cref="UserInfoFixtures.FromUser"/> for a Users <c>Profile</c> entity, which the shared fixture
    /// can't see.
    /// </summary>
    public static UserInfo FromEntities(User user, Profile? profile) =>
        UserInfoFactory.Create(user, user.UserEmails?.ToList() ?? [], [], profile, [], [], []);

    public static IUserService StubGetUserInfosFromContext(this IUserService userService, UsersDbContext dbContext)
    {
        userService
            .GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<Guid>>();
                if (ids.Count == 0)
                    return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                        new Dictionary<Guid, UserInfo>());
                var users = dbContext.Users.AsNoTracking()
                    .Include(u => u.UserEmails)
                    .Where(u => ids.Contains(u.Id))
                    .ToList();
                var profiles = dbContext.Profiles.AsNoTracking()
                    .Where(p => ids.Contains(p.UserId))
                    .ToDictionary(p => p.UserId);
                IReadOnlyDictionary<Guid, UserInfo> dict = users.ToDictionary(
                    u => u.Id,
                    u => FromEntities(u, profiles.GetValueOrDefault(u.Id)));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });
        return userService;
    }
}
