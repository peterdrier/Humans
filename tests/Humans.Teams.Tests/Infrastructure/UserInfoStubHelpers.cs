using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Domain;
using Humans.Users.Services;

namespace Humans.Teams.Tests.Infrastructure;

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
    public static UserInfo FromEntities(User user, Profile? profile) =>
        UserInfoFactory.Create(user, user.UserEmails?.ToList() ?? [], [], profile, [], [], []);

    /// <summary>
    /// Opens a new context per call.
    /// </summary>
    public static IUserService StubGetUserInfosFromDb(this IUserService userService, DbContextOptions<UsersDbContext> options)
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
    /// Opens a new context per call.
    /// </summary>
    public static IUserService StubGetAllUserInfosFromDb(this IUserService userService, DbContextOptions<UsersDbContext> options)
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
    /// Reuses one shared context.
    /// </summary>
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

    /// <summary>
    /// Stubs the singular GetUserInfoAsync to read from a long-lived DbContext, mirroring
    /// <see cref="StubGetUserInfosFromContext"/>. Returns null for unknown ids.
    /// </summary>
    public static IUserService StubGetUserInfoFromContext(this IUserService userService, UsersDbContext dbContext)
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
