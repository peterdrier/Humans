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
    {
        var resolvedProfile = profile ?? new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = displayName,
            CreatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant(),
            IsApproved = true
        };
        return UserInfoFactory.Create(
            // UserInfo.BurnerName reads User.BurnerName, so it is seeded there.
            new User { Id = userId, PreferredLanguage = "en", BurnerName = resolvedProfile.BurnerName },
            [],
            [],
            [],
            profile: resolvedProfile,
            [],
            [],
            [],
            []);
    }

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
