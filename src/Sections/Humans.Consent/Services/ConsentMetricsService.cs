using System.Diagnostics.Metrics;
using Humans.Base.Hosting;
using Humans.Consent.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Humans.Consent.Services;

/// <summary>
/// Owns the Consent-section observable gauge split out of <c>HumansMetricsService</c>
/// (nobodies-collective/Humans#1091): count of active+required legal documents.
/// </summary>
internal sealed class ConsentMetricsService : PolledGaugeService
{
    private static readonly Meter HumansMeter = new("Humans.Metrics");

    private volatile int _legalDocumentsActive;

    public ConsentMetricsService(IServiceScopeFactory scopeFactory, ILogger<ConsentMetricsService> logger)
        : base(scopeFactory, logger)
    {
        HumansMeter.CreateObservableGauge(
            "humans.legal_documents_active",
            observeValue: () => _legalDocumentsActive,
            description: "Active required legal documents");
    }

    protected override async Task RefreshAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var legalDocumentSyncService = scope.ServiceProvider.GetRequiredService<ILegalDocumentSyncServiceRead>();
        _legalDocumentsActive = await legalDocumentSyncService.GetActiveRequiredCountAsync();
    }
}
