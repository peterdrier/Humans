using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Domain;
using Humans.Users.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Humans.Testing;

/// <summary>
/// Stubs the user-info readers from the test's in-memory Users store: the User, its UserEmails and Profile, empty
/// collections for the rest.
/// </summary>
internal static class UserInfoStubHelpers
{
    /// <summary><see cref="UserInfoFixtures.FromUser"/> with a Users <c>Profile</c> entity.</summary>
    public static UserInfo FromEntities(User user, Profile? profile, IReadOnlyList<UserEmail>? userEmails = null) =>
        UserInfoFactory.Create(user, userEmails ?? user.UserEmails.ToList(), [], profile, [], [], []);

    /// <summary>Opens a new context per call.</summary>
    public static T StubGetUserInfosFromDb<T>(this T userService, DbContextOptions<UsersDbContext> options)
        where T : IUserService
    {
        userService
            .GetUserInfosAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                using var db = new UsersDbContext(options);
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(Read(db, callInfo.Arg<IEnumerable<Guid>>()));
            });
        return userService;
    }

    /// <summary>Opens a new context per call.</summary>
    public static T StubAllUserInfosFromDb<T>(this T userService, DbContextOptions<UsersDbContext> options)
        where T : IUserService
    {
        userService
            .GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                using var db = new UsersDbContext(options);
                IReadOnlyCollection<UserInfo> result = Read(db, null).Values.ToList();
                return Task.FromResult(result);
            });
        return userService;
    }

    /// <summary>Reuses one shared context.</summary>
    public static T StubGetUserInfosFromContext<T>(this T userService, UsersDbContext dbContext)
        where T : IUserService
    {
        userService
            .GetUserInfosAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(Read(dbContext, callInfo.Arg<IEnumerable<Guid>>())));
        return userService;
    }

    /// <summary>Reuses one shared context. Unknown ids read as null.</summary>
    public static T StubGetUserInfoFromContext<T>(this T userService, UsersDbContext dbContext)
        where T : IUserService
    {
        userService
            .GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                new ValueTask<UserInfo?>(Read(dbContext, [callInfo.Arg<Guid>()]).Values.SingleOrDefault()));
        return userService;
    }

    /// <summary>The users with <paramref name="ids"/>, or every user when null.</summary>
    private static Dictionary<Guid, UserInfo> Read(UsersDbContext db, IEnumerable<Guid>? ids)
    {
        var wanted = ids?.ToList();
        if (wanted is { Count: 0 })
            return [];

        var users = db.Users.AsNoTracking()
            .Include(u => u.UserEmails)
            .Where(u => wanted == null || wanted.Contains(u.Id))
            .ToList();
        var userIds = users.Select(u => u.Id).ToList();
        var profiles = db.Profiles.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .ToDictionary(p => p.UserId);
        return users.ToDictionary(u => u.Id, u => FromEntities(u, profiles.GetValueOrDefault(u.Id)));
    }
}
