using Humans.AuditLog.Contracts;
using Microsoft.Extensions.Hosting;

namespace Humans.AuditLog.Services;

/// <summary>
/// Every section's audit actions, collected once at startup. Registered as a hosted service only so the host
/// constructs it on boot: a duplicate or over-long name fails the start instead of the first audit read.
/// </summary>
internal sealed class AuditActionRegistry : IHostedService
{
    private readonly Dictionary<string, AuditActionDefinition> _byName;

    public AuditActionRegistry(IEnumerable<IAuditActionContributor> contributors)
    {
        var actions = contributors.SelectMany(c => c.Actions).ToList();

        var invalid = actions
            .Where(a => string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > AuditActionDefinition.NameMaxLength)
            .Select(a => $"'{a.Name}'")
            .ToList();
        if (invalid.Count > 0)
            throw new InvalidOperationException(
                $"Audit action names must be 1 to {AuditActionDefinition.NameMaxLength} characters: {string.Join(", ", invalid)}.");

        var duplicates = actions
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"Audit action names are declared more than once (ignoring case, as filters match): {string.Join(", ", duplicates)}.");

        _byName = actions.ToDictionary(a => a.Name, StringComparer.Ordinal);
        Alerts = actions.Where(a => a.Alert).ToList();
        AlertNames = Alerts.Select(a => a.Name).ToList();
        QuickFilters = actions.Where(a => a.QuickFilter is not null).ToList();
    }

    /// <summary>A stored name nobody declares any more still renders: as a bare definition with no verb.</summary>
    public AuditActionDefinition Resolve(string name) =>
        _byName.TryGetValue(name, out var definition) ? definition : new AuditActionDefinition(name);

    /// <summary>The declared name that matches ignoring case, so a bookmarked filter keeps working; else the input.</summary>
    public string CanonicalName(string name) =>
        _byName.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;

    public IReadOnlyList<AuditActionDefinition> Alerts { get; }

    public IReadOnlyList<string> AlertNames { get; }

    /// <summary>In contributor order, then declaration order.</summary>
    public IReadOnlyList<AuditActionDefinition> QuickFilters { get; }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
