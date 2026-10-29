using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Notifications.Services;
using Humans.Users.Contracts;
using NSubstitute;

namespace Humans.Notifications.Tests;

/// <summary>
/// Fixtures owned here rather than shared: a section test project cannot see
/// <c>EmailDbContext</c>, and the preference lookup the emitter calls is a substituted
/// interface either way.
/// </summary>
internal static class NotificationTestFixtures
{
    /// <summary>
    /// Holds the <c>email_communication_preferences</c> rows the emitter tests seed, behind
    /// <c>.CommunicationPreferences.Add(…)</c> and <c>.SaveChangesAsync(ct)</c>.
    /// </summary>
    internal sealed class PreferenceRegistry
    {
        public List<PreferenceRow> CommunicationPreferences { get; } = [];

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// The columns of an <c>email_communication_preferences</c> row these tests seed. Owned
    /// here rather than the section's entity, which is internal to <c>Humans.Email</c>.
    /// </summary>
    internal sealed class PreferenceRow
    {
        public Guid UserId { get; init; }

        public MessageCategory Category { get; init; }

        public bool InboxEnabled { get; init; } = true;
    }

    /// <summary>
    /// Stubs <see cref="ICommunicationPreferenceService.GetUsersWithInboxDisabledAsync"/> to
    /// read from <paramref name="registry"/>.
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
        UserInfo.Create(user, [], [], [], profile: null, []);
}

/// <summary>Sources these tests own: the section's tests never borrow another section's definitions.</summary>
internal sealed class TestSources : NotificationSourceContributor
{
    public static readonly NotificationSourceDefinition TeamUpdate = new("TestTeamUpdate", MessageCategory.TeamUpdates);
    public static readonly NotificationSourceDefinition ShiftUpdate = new("TestShiftUpdate", MessageCategory.VolunteerUpdates, NotificationInboxGroup.Shifts);
    public static readonly NotificationSourceDefinition Approval = new("TestApproval", MessageCategory.Governance, NotificationInboxGroup.Approvals);
    public static readonly NotificationSourceDefinition Plain = new("TestPlain", MessageCategory.System);
    public static readonly NotificationSourceDefinition Retired = new("TestRetired", MessageCategory.Governance, NotificationInboxGroup.Approvals, Retired: true);
    public static readonly NotificationSourceDefinition RetiredToo = new("TestRetiredToo", MessageCategory.System, Retired: true);

    public static NotificationSourceRegistry Registry => new([new TestSources()]);
}
