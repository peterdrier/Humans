using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Services;

namespace Humans.Users.Tests.Infrastructure;

/// <summary>
/// Stubs the user-info readers from the test's in-memory DB: the User and its UserEmails,
/// empty collections for the rest.
/// </summary>
internal static class UserInfoStubHelpers
{
    /// <summary>
    /// <see cref="UserInfoFixtures.FromUser"/> for a Users <c>Profile</c> entity, which the shared fixture
    /// can't see.
    /// </summary>
    public static UserInfo FromEntities(User user, Profile? profile, IReadOnlyList<UserEmail>? userEmails = null) =>
        UserInfoFactory.Create(user, userEmails ?? user.UserEmails?.ToList() ?? [], [], profile, [], [], []);

    /// <summary>
    /// Stubs GetUserInfosAsync to read from the provided DbContext options (new context per call,
    /// includes UserEmails + Profile slice).
    /// </summary>
    public static IUserManagement StubGetUserInfosFromDb(this IUserManagement userService, DbContextOptions<UsersDbContext> options)
    {
        userService
            .GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<Guid>>();
                if (ids.Count == 0)
                    return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                        new Dictionary<Guid, UserInfo>());
                using var db = new UsersDbContext(options);
                var users = db.Users.AsNoTracking()
                    .Include(u => u.UserEmails)
                    .Where(u => ids.Contains(u.Id))
                    .ToList();
                var profiles = db.Profiles.AsNoTracking()
                    .Where(p => ids.Contains(p.UserId))
                    .ToDictionary(p => p.UserId);
                IReadOnlyDictionary<Guid, UserInfo> dict = users.ToDictionary(
                    u => u.Id,
                    u => FromEntities(u, profiles.GetValueOrDefault(u.Id)));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });
        return userService;
    }

    /// <summary>
    /// Stubs GetAllUserInfosAsync to read from the provided DbContext options
    /// (new context per call, includes UserEmails + Profile slice).
    /// </summary>
    public static IUserManagement StubGetAllUserInfosFromDb(this IUserManagement userService, DbContextOptions<UsersDbContext> options)
    {
        userService
            .GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                using var db = new UsersDbContext(options);
                var users = db.Users.AsNoTracking()
                    .Include(u => u.UserEmails)
                    .ToList();
                var userIds = users.Select(u => u.Id).ToList();
                var profiles = db.Profiles.AsNoTracking()
                    .Where(p => userIds.Contains(p.UserId))
                    .ToDictionary(p => p.UserId);
                IReadOnlyCollection<UserInfo> result = users
                    .Select(u => FromEntities(u, profiles.GetValueOrDefault(u.Id)))
                    .ToList();
                return Task.FromResult(result);
            });
        return userService;
    }

    /// <summary>
    /// Stubs GetUserInfosAsync to read from a long-lived DbContext (uses AsNoTracking but reuses
    /// the same instance — fine for in-memory tests that share one ctx).
    /// </summary>
    public static IUserManagement StubGetUserInfosFromContext(this IUserManagement userService, UsersDbContext dbContext)
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

    /// <summary>
    /// Stubs the singular GetUserInfoAsync to read from a long-lived DbContext, mirroring
    /// <see cref="StubGetUserInfosFromContext"/>. Returns null for unknown ids.
    /// </summary>
    public static IUserManagement StubGetUserInfoFromContext(this IUserManagement userService, UsersDbContext dbContext)
    {
        userService
            .GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(async ValueTask<UserInfo?> (callInfo) =>
            {
                var id = callInfo.Arg<Guid>();
                var ct = callInfo.Arg<CancellationToken>();
                var user = await dbContext.Users.AsNoTracking()
                    .Include(u => u.UserEmails)
                    .FirstOrDefaultAsync(u => u.Id == id, ct);
                if (user is null)
                    return null;
                var profile = await dbContext.Profiles.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.UserId == id, ct);
                return FromEntities(user, profile);
            });
        return userService;
    }
}
