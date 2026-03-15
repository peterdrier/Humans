using Humans.Domain.Entities;

namespace Humans.Application.Interfaces;

public record WaveSendPreview(
    int EligibleCount,
    int AlreadyGrantedExcluded,
    int UnsubscribedExcluded,
    int CodesAvailable,
    int CodesRemainingAfterSend);

public interface ICampaignService
{
    Task<Campaign> CreateAsync(string title, string? description,
        string emailSubject, string emailBodyTemplate, string? replyToAddress,
        Guid createdByUserId, CancellationToken ct = default);
    Task<Campaign?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<Campaign>> GetAllAsync(CancellationToken ct = default);
    Task ImportCodesAsync(Guid campaignId, IEnumerable<string> codes, CancellationToken ct = default);
    Task ImportGeneratedCodesAsync(Guid campaignId, IReadOnlyList<string> codes, CancellationToken ct = default);
    Task ActivateAsync(Guid campaignId, CancellationToken ct = default);
    Task CompleteAsync(Guid campaignId, CancellationToken ct = default);
    Task<WaveSendPreview> PreviewWaveSendAsync(Guid campaignId, Guid teamId, CancellationToken ct = default);
    Task<int> SendWaveAsync(Guid campaignId, Guid teamId, CancellationToken ct = default);
    Task ResendToGrantAsync(Guid grantId, CancellationToken ct = default);
    Task RetryAllFailedAsync(Guid campaignId, CancellationToken ct = default);
}
