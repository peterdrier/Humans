using Humans.Users.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Application;
using Humans.Domain.Enums;
using NSubstitute;

namespace Humans.Notifications.Tests;

/// <summary>
/// The three members of <c>Humans.Application.Tests</c>' harness these tests actually used,
/// owned here instead of shared: a section test project cannot see <c>UsersDbContext</c>,
/// and the preference lookup the emitter calls is a substituted interface either way
/// (design §15 step 8).
/// </summary>
internal static class NotificationTestFixtures
{
    /// <summary>
    /// A stand-in for the in-memory <c>UsersDbContext</c> the emitter tests used to hold
    /// <c>communication_preferences</c> rows in. Keeps <c>.CommunicationPreferences.Add(…)</c>
    /// and <c>.SaveChangesAsync(ct)</c> so the test bodies are unchanged.
    /// </summary>
    internal sealed class PreferenceRegistry
    {
        public List<CommunicationPreference> CommunicationPreferences { get; } = [];

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Stubs <see cref="ICommunicationPreferenceService.GetUsersWithInboxDisabledAsync"/> to
    /// read from <paramref name="registry"/>, matching the old DB-backed stub's behaviour.
    /// </summary>
    internal static ICommunicationPreferenceService StubInboxDisabledFrom(
        this ICommunicationPreferenceService service, PreferenceRegistry registry)
    {
        service.GetUsersWithInboxDisabledAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<MessageCategory>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var userIds = callInfo.Arg<IReadOnlyList<Guid>>();
                var category = callInfo.Arg<MessageCategory>();
                var disabledIds = registry.CommunicationPreferences
                    .Where(cp => userIds.Contains(cp.UserId) && cp.Category == category && !cp.InboxEnabled)
                    .Select(cp => cp.UserId)
                    .ToHashSet();
                return Task.FromResult<IReadOnlySet<Guid>>(disabledIds);
            });
        return service;
    }

    /// <summary>Minimal <see cref="UserInfo"/> projection — the meter tests only read counts.</summary>
    internal static UserInfo ToUserInfo(this User user) =>
        UserInfo.Create(user, [], [], [], profile: null, [], [], [], []);
}
