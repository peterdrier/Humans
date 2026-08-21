using Humans.MailerLite.Services;
using Humans.MailerLite.Services.Dtos;
using NodaTime;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="IMailerLiteService"/> so integration tests never reach
/// connect.mailerlite.com — the same call as <c>StripeServiceStub</c>, made by hand rather
/// than with NSubstitute because the interface exposes an
/// <see cref="IAsyncEnumerable{T}"/> that a default substitute returns null for.
/// </summary>
/// <remarks>
/// Defaults model the smallest account the section's pages need to be worth rendering: the
/// <c>Website</c> group the import reads (resolved by name, and
/// <c>MailerLiteImportService.BuildPlanAsync</c> throws if it is absent) plus whatever
/// subscribers a test adds. The four audience-management writes record their calls and
/// mutate nothing; <see cref="DeleteSubscriberAsync"/> actually removes the subscriber so
/// GDPR erasure tests can observe the effect.
/// </remarks>
internal sealed class StubMailerLiteService : IMailerLiteService
{
    public const string WebsiteGroupId = "grp-website";

    private readonly List<MailerLiteGroup> _groups =
    [
        new(WebsiteGroupId, "Website", Instant.FromUtc(2026, 1, 1, 0, 0), 0, 0, 0, 0, 0),
    ];

    private readonly List<MailerLiteSubscriber> _subscribers = [];

    public Instant? LastFetchedAt { get; private set; }

    /// <summary>Adds an active subscriber sitting in the <c>Website</c> group.</summary>
    public void AddWebsiteSubscriber(string email) =>
        _subscribers.Add(new MailerLiteSubscriber(
            Id: $"sub-{_subscribers.Count + 1}",
            Email: email,
            Status: "active",
            Source: "api",
            SubscribedAt: Instant.FromUtc(2026, 2, 1, 0, 0),
            UnsubscribedAt: null,
            OptedInAt: Instant.FromUtc(2026, 2, 1, 0, 0),
            FirstName: null,
            LastName: null,
            GroupIds: [WebsiteGroupId]));

    public Task<MailerLiteAccountSummary> GetAccountSummaryAsync(CancellationToken ct = default)
    {
        LastFetchedAt = Instant.FromUtc(2026, 2, 1, 0, 0);
        return Task.FromResult(new MailerLiteAccountSummary(_subscribers.Count, 0, 0, 0, 0));
    }

    public Task<IReadOnlyList<MailerLiteGroup>> ListGroupsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MailerLiteGroup>>(_groups);

    public async IAsyncEnumerable<MailerLiteSubscriber> ListSubscribersAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var s in _subscribers)
        {
            ct.ThrowIfCancellationRequested();
            yield return s;
        }
        await Task.CompletedTask;
    }

    public Task<MailerLiteSubscriber?> GetSubscriberAsync(string email, CancellationToken ct = default) =>
        Task.FromResult(_subscribers.Find(s =>
            string.Equals(s.Email, email, StringComparison.OrdinalIgnoreCase)));

    public Task RefreshAsync(CancellationToken ct = default)
    {
        LastFetchedAt = Instant.FromUtc(2026, 2, 1, 0, 0);
        return Task.CompletedTask;
    }

    public Task<MailerLiteGroup> CreateGroupAsync(string name, CancellationToken ct = default)
    {
        var group = new MailerLiteGroup(
            $"grp-{_groups.Count + 1}", name, Instant.FromUtc(2026, 1, 1, 0, 0), 0, 0, 0, 0, 0);
        _groups.Add(group);
        return Task.FromResult(group);
    }

    public Task AssignSubscriberToGroupAsync(string subscriberId, string groupId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UnassignSubscriberFromGroupAsync(string subscriberId, string groupId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<BulkImportResult> BulkImportSubscribersToGroupAsync(
        string groupId, IReadOnlyList<string> emails, CancellationToken ct = default) =>
        Task.FromResult(new BulkImportResult(emails.Count, 0, 0, 0));

    public Task DeleteSubscriberAsync(string email, CancellationToken ct = default)
    {
        _subscribers.RemoveAll(s => string.Equals(s.Email, email, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }
}
