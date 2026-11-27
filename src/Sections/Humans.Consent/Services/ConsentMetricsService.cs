using System.Diagnostics.Metrics;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Consent.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Consent.Services;

internal sealed class ConsentMetricsService : PolledGaugeService
{
    private static readonly Meter HumansMeter = new("Humans.Metrics");

    private volatile GaugeSnapshot _snapshot = GaugeSnapshot.Empty;

    public ConsentMetricsService(IServiceScopeFactory scopeFactory, ILogger<ConsentMetricsService> logger)
        : base(scopeFactory, logger)
    {
        HumansMeter.CreateObservableGauge(
            "humans.legal_documents_active",
            observeValue: () => _snapshot.LegalDocumentsActive,
            description: "Active required legal documents");

        HumansMeter.CreateObservableGauge(
            "humans.pending_consents",
            observeValue: () => _snapshot.PendingConsents,
            description: "Users missing required consents");

        HumansMeter.CreateObservableGauge(
            "humans.consent_deadline_approaching",
            observeValue: () => _snapshot.ConsentDeadlineApproaching,
            description: "Users past grace period not yet suspended");
    }

    protected override async Task RefreshAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var legalDocumentSyncService = scope.ServiceProvider.GetRequiredService<ILegalDocumentSyncManagement>();
        var consentService = scope.ServiceProvider.GetRequiredService<IConsentService>();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        var userInfos = await userService.GetAllUserInfosAsync().ConfigureAwait(false);
        var allUserIds = userInfos.Select(u => u.Id).ToList();
        var usersWithAllConsents = await consentService.GetConsentedUsersForTeamAsync(allUserIds, SystemTeamIds.Volunteers);
        var usersPastDeadline = await consentService.GetUsersPastConsentDeadlineAsync();

        _snapshot = new GaugeSnapshot
        {
            LegalDocumentsActive = await legalDocumentSyncService.GetActiveRequiredCountAsync(),
            PendingConsents = allUserIds.Count - usersWithAllConsents.Count,
            ConsentDeadlineApproaching = usersPastDeadline.Count,
        };
    }

    private sealed record GaugeSnapshot
    {
        public static readonly GaugeSnapshot Empty = new();

        public int LegalDocumentsActive { get; init; }
        public int PendingConsents { get; init; }
        public int ConsentDeadlineApproaching { get; init; }
    }
}
