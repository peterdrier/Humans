using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Humans.Application.Tests.Infrastructure;

/// <summary>
/// Helpers for stubbing the <see cref="IUserService.GetUserInfosAsync"/> reader on
/// NSubstitute test doubles, mirroring whatever in-memory DB the existing
/// <c>GetByIdsAsync</c> stubs read from. Builds a minimal UserInfo
/// (the User + its UserEmails) — empty collections for the rest, which
/// matches what existing legacy stubs covered.
/// </summary>
internal static class UserInfoStubHelpers
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

    public static UserInfo MakeUserInfo(Guid userId, Profile? profile = null, string displayName = "User")
        => UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en" },
            [],
            [],
            [],
            profile: profile ?? new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BurnerName = displayName,
                CreatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant(),
                UpdatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant(),
                State = Humans.Domain.Enums.ProfileState.Active,
                IsApproved = true
            },
            [],
            [],
            [],
            []);

    /// <summary>
    /// Stubs GetUserInfosAsync to read from the provided DbContext options (new context per call,
    /// includes UserEmails + Profile slice).
    /// </summary>
    public static IUserService StubGetUserInfosFromDb(this IUserService userService, DbContextOptions<HumansDbContext> options)
    {
        userService
            .GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<Guid>>();
                if (ids.Count == 0)
                    return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                        new Dictionary<Guid, UserInfo>());
                using var db = new HumansDbContext(options);
                var users = db.Users.AsNoTracking()
                    .Include(u => u.UserEmails)
                    .Where(u => ids.Contains(u.Id))
                    .ToList();
                var profiles = db.Profiles.AsNoTracking()
                    .Where(p => ids.Contains(p.UserId))
                    .ToDictionary(p => p.UserId);
                IReadOnlyDictionary<Guid, UserInfo> dict = users.ToDictionary(
                    u => u.Id,
                    u => u.ToUserInfo(u.UserEmails.ToList(), profiles.GetValueOrDefault(u.Id)));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });
        return userService;
    }

    /// <summary>
    /// Stubs GetAllUserInfosAsync to read from the provided DbContext options
    /// (new context per call, includes UserEmails + Profile slice).
    /// </summary>
    public static IUserService StubGetAllUserInfosFromDb(this IUserService userService, DbContextOptions<HumansDbContext> options)
    {
        userService
            .GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                using var db = new HumansDbContext(options);
                var users = db.Users.AsNoTracking()
                    .Include(u => u.UserEmails)
                    .ToList();
                var userIds = users.Select(u => u.Id).ToList();
                var profiles = db.Profiles.AsNoTracking()
                    .Where(p => userIds.Contains(p.UserId))
                    .ToDictionary(p => p.UserId);
                IReadOnlyCollection<UserInfo> result = users
                    .Select(u => u.ToUserInfo(u.UserEmails.ToList(), profiles.GetValueOrDefault(u.Id)))
                    .ToList();
                return Task.FromResult(result);
            });
        return userService;
    }

    /// <summary>
    /// Stubs GetUserInfosAsync to read from a long-lived DbContext (uses AsNoTracking but reuses
    /// the same instance — fine for in-memory tests that share one ctx).
    /// </summary>
    public static IUserService StubGetUserInfosFromContext(this IUserService userService, HumansDbContext dbContext)
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
                    u => u.ToUserInfo(u.UserEmails.ToList(), profiles.GetValueOrDefault(u.Id)));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });
        return userService;
    }

    /// <summary>
    /// Stubs the singular GetUserInfoAsync to read from a long-lived DbContext, mirroring
    /// <see cref="StubGetUserInfosFromContext"/>. Returns null for unknown ids.
    /// </summary>
    public static IUserService StubGetUserInfoFromContext(this IUserService userService, HumansDbContext dbContext)
    {
        userService
            .GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var id = callInfo.Arg<Guid>();
                var user = dbContext.Users.AsNoTracking()
                    .Include(u => u.UserEmails)
                    .FirstOrDefault(u => u.Id == id);
                if (user is null)
                    return new ValueTask<UserInfo?>((UserInfo?)null);
                var profile = dbContext.Profiles.AsNoTracking()
                    .FirstOrDefault(p => p.UserId == id);
                return new ValueTask<UserInfo?>(user.ToUserInfo(user.UserEmails.ToList(), profile));
            });
        return userService;
    }
}
