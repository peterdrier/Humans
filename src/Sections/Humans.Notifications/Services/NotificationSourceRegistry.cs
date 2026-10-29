using Humans.Notifications.Contracts;
using Microsoft.Extensions.Hosting;

namespace Humans.Notifications.Services;

/// <summary>
/// Every section's notification sources, collected once at startup. Registered as a hosted service only so the
/// host constructs it on boot: a duplicate or over-long name fails the start instead of the first inbox read.
/// </summary>
internal sealed class NotificationSourceRegistry : IHostedService
{
    private readonly IReadOnlyList<NotificationSourceDefinition> _sources;

    public NotificationSourceRegistry(IEnumerable<INotificationSourceContributor> contributors)
    {
        _sources = contributors.SelectMany(c => c.Sources).ToList();

        var invalid = _sources
            .Where(s => string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > NotificationSourceDefinition.NameMaxLength)
            .Select(s => $"'{s.Name}'")
            .ToList();
        if (invalid.Count > 0)
            throw new InvalidOperationException(
                $"Notification source names must be 1 to {NotificationSourceDefinition.NameMaxLength} characters: {string.Join(", ", invalid)}.");

        var duplicates = _sources
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"Notification source names are declared more than once: {string.Join(", ", duplicates)}.");

        RetiredNames = _sources.Where(s => s.Retired).Select(s => s.Name).ToList();
    }

    public IReadOnlyList<string> RetiredNames { get; }

    public IReadOnlyList<string> NamesIn(NotificationInboxGroup group) =>
        _sources.Where(s => s.InboxGroup == group).Select(s => s.Name).ToList();

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
