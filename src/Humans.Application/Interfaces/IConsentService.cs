using Humans.Domain.Entities;

namespace Humans.Application.Interfaces;

public record ConsentSubmitResult(bool Success, string? DocumentName = null, string? ErrorKey = null);

public interface IConsentService
{
    Task<(List<(Team Team, List<(DocumentVersion Version, ConsentRecord? Consent)> Documents)> Groups,
          List<ConsentRecord> History)>
        GetConsentDashboardAsync(Guid userId, CancellationToken ct = default);

    Task<(DocumentVersion? Version, ConsentRecord? ExistingConsent, string? UserFullName)>
        GetConsentReviewDetailAsync(Guid documentVersionId, Guid userId, CancellationToken ct = default);

    Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default);
}
