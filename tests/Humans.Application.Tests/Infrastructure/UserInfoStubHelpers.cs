using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Humans.Application.Tests.Infrastructure;

/// <summary>
/// Helpers for stubbing the new <see cref="IUserService.GetUserInfosAsync"/> reader on
/// NSubstitute test doubles, mirroring whatever in-memory DB the existing
/// <c>GetByIdsAsync</c> / <c>GetByIdsWithEmailsAsync</c> stubs read from.
/// Builds a minimal UserInfo (the User + its UserEmails) — empty collections for
/// the rest, which matches what existing legacy stubs covered.
/// </summary>
internal static class UserInfoStubHelpers
{
    public static UserInfo ToUserInfo(this User user, IReadOnlyList<UserEmail>? userEmails = null)
        => UserInfo.Create(
            user,
            userEmails ?? user.UserEmails?.ToList() ?? new List<UserEmail>(),
            Array.Empty<EventParticipation>(),
            Array.Empty<(string, string)>(),
            profile: null,
            Array.Empty<ContactField>(),
            Array.Empty<ProfileLanguage>(),
            Array.Empty<VolunteerHistoryEntry>(),
            Array.Empty<CommunicationPreference>());

    /// <summary>
    /// Stubs GetUserInfosAsync to read from the provided DbContext options (new context per call,
    /// includes UserEmails).
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
                IReadOnlyDictionary<Guid, UserInfo> dict = users.ToDictionary(
                    u => u.Id,
                    u => u.ToUserInfo(u.UserEmails.ToList()));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
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
                IReadOnlyDictionary<Guid, UserInfo> dict = users.ToDictionary(
                    u => u.Id,
                    u => u.ToUserInfo(u.UserEmails.ToList()));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });
        return userService;
    }
}
