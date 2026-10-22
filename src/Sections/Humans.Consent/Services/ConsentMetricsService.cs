using System.Diagnostics.Metrics;
using Humans.Base.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Consent.Services;

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
        var legalDocumentSyncService = scope.ServiceProvider.GetRequiredService<ILegalDocumentSyncManagement>();
        _legalDocumentsActive = await legalDocumentSyncService.GetActiveRequiredCountAsync();
    }
}
