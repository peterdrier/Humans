using System.Diagnostics.Metrics;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Application.Services.Consent;

/// <summary>
/// Consent section's push-model metrics contributor (issue nobodies-collective/Humans#580).
/// Owns <c>humans.consents_given_total</c> and the two consent-related gauges
/// <c>humans.pending_consents</c> / <c>humans.consent_deadline_approaching</c>,
/// both of which are sourced from <see cref="IMembershipCalculator"/>.
/// </summary>
/// <remarks>
/// Singleton so the counter reference and snapshot state persist across scopes.
/// The gauge refresher opens a scope each tick to resolve the scoped
/// <see cref="IMembershipCalculator"/> and <see cref="IUserService"/> safely.
/// </remarks>
public sealed class ConsentMetricsContributor : IConsentMetrics, IMetricsContributor
{
    private readonly IServiceScopeFactory _scopeFactory;

    private Counter<long> _consentsGiven = null!;
    private volatile int _pendingConsents;
    private volatile int _consentDeadlineApproaching;

    public ConsentMetricsContributor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Initialize(IHumansMetrics metrics)
    {
        _consentsGiven = metrics.CreateCounter<long>(
            "humans.consents_given_total",
            description: "Total consent records created");

        metrics.CreateObservableGauge<int>(
            "humans.pending_consents",
            observeValue: () => _pendingConsents,
            description: "Users missing required consents");

        metrics.CreateObservableGauge<int>(
            "humans.consent_deadline_approaching",
            observeValue: () => _consentDeadlineApproaching,
            description: "Users past grace period not yet suspended");

        metrics.RegisterGaugeRefresher(RefreshAsync);
    }

    public void RecordConsentGiven() => _consentsGiven.Add(1);

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var membershipCalc = scope.ServiceProvider.GetRequiredService<IMembershipCalculator>();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        var allUserIds = await userService.GetAllUserIdsAsync(cancellationToken);
        var usersWithAllConsents = await membershipCalc.GetUsersWithAllRequiredConsentsAsync(allUserIds);
        _pendingConsents = allUserIds.Count - usersWithAllConsents.Count;

        var usersRequiringUpdate = await membershipCalc.GetUsersRequiringStatusUpdateAsync();
        _consentDeadlineApproaching = usersRequiringUpdate.Count;
    }
}
