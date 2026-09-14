using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Domain;
using Humans.Users.Services;

namespace Humans.Shifts.Tests.Infrastructure;

/// <summary>
/// Helpers for stubbing the <see cref="IUserService.GetUserInfosAsync"/> reader on
/// NSubstitute test doubles, reading from whatever in-memory DB the test
/// owns. Builds a minimal UserInfo
/// (the User + its UserEmails) — empty collections for the rest, which
/// matches what existing legacy stubs covered.
/// </summary>
internal static class UserInfoStubHelpers
{
    public static UserInfo ToUserInfo(
        this User user,
        IReadOnlyList<UserEmail>? userEmails = null,
        Profile? profile = null)
        => UserInfoFactory.Create(
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
        => UserInfoFactory.Create(
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
                IsApproved = true
            },
            [],
            [],
            [],
            []);

    /// <summary>
    /// Stubs GetUserInfosAsync to read from a long-lived DbContext (uses AsNoTracking but reuses
    /// the same instance — fine for in-memory tests that share one ctx).
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
                    u => u.ToUserInfo(u.UserEmails.ToList(), profiles.GetValueOrDefault(u.Id)));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });
        return userService;
    }
}
