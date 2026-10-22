using Humans.MailerLite.Services;
using Humans.MailerLite.Services.Dtos;
using NodaTime;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// Hand-written because a substitute returns null for the <see cref="IAsyncEnumerable{T}"/>. Seeds the <c>Website</c>
/// group the import needs (<c>BuildPlanAsync</c> throws without it). The audience writes record calls and change
/// nothing; <see cref="DeleteSubscriberAsync"/> really removes, so GDPR erasure tests can observe it.
/// </summary>
internal sealed class StubMailerLiteService : IMailerLiteService
{
    public const string WebsiteGroupId = "grp-website";

    private readonly List<MailerLiteGroup> _groups =
    [
        new(WebsiteGroupId, "Website", 0, 0, 0, 0, 0),
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

    public Task RefreshAsync(CancellationToken ct = default)
    {
        LastFetchedAt = Instant.FromUtc(2026, 2, 1, 0, 0);
        return Task.CompletedTask;
    }

    public Task<MailerLiteGroup> CreateGroupAsync(string name, CancellationToken ct = default)
    {
        var group = new MailerLiteGroup(
            $"grp-{_groups.Count + 1}", name, 0, 0, 0, 0, 0);
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
