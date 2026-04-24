using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Legal;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Application.Services.Legal;

/// <summary>
/// Legal section's push-model metrics contributor (issue nobodies-collective/Humans#580).
/// Gauge-only. Owns <c>humans.legal_documents_active</c>.
/// </summary>
public sealed class LegalMetricsContributor : IMetricsContributor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private volatile int _activeRequiredCount;

    public LegalMetricsContributor(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Initialize(IHumansMetrics metrics)
    {
        metrics.CreateObservableGauge<int>(
            "humans.legal_documents_active",
            observeValue: () => _activeRequiredCount,
            description: "Active required legal documents");

        metrics.RegisterGaugeRefresher(RefreshAsync);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<ILegalDocumentSyncService>();

        // Active documents include both Required and non-Required; filter in
        // memory — the set is small (≈5 documents).
        var activeDocs = await syncService.GetActiveDocumentsAsync(cancellationToken);
        _activeRequiredCount = activeDocs.Count(d => d.IsRequired);
    }
}
