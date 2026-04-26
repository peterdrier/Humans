using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Metering;

/// <summary>
/// Registers the Legal section's <c>humans.legal_documents_active</c> gauge
/// with <see cref="IMeters"/> at app startup. A 60-second tick refreshes the
/// cached count via <see cref="IAdminLegalDocumentService"/>; the OTel
/// observable callback reads the cached field synchronously.
/// </summary>
public sealed class LegalMetricsRegistrar : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IMeters _meters;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LegalMetricsRegistrar> _logger;
    private Timer? _refreshTimer;

    private volatile int _legalDocumentsActive;

    public LegalMetricsRegistrar(
        IMeters meters,
        IServiceScopeFactory scopeFactory,
        ILogger<LegalMetricsRegistrar> logger)
    {
        _meters = meters;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _meters.RegisterObservableGauge(
            "humans.legal_documents_active",
            new MeterMetadata("Active required legal documents", "{documents}"),
            () => _legalDocumentsActive);

        _refreshTimer = new Timer(
            callback: _ => _ = RefreshAsync(),
            state: null,
            dueTime: TimeSpan.Zero,
            period: RefreshInterval);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_refreshTimer is not null)
        {
            await _refreshTimer.DisposeAsync();
            _refreshTimer = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_refreshTimer is not null)
        {
            await _refreshTimer.DisposeAsync();
            _refreshTimer = null;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var adminLegalDocumentService = scope.ServiceProvider
                .GetRequiredService<IAdminLegalDocumentService>();

            _legalDocumentsActive = await adminLegalDocumentService.GetActiveRequiredDocumentCountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Legal section metrics");
        }
    }
}
