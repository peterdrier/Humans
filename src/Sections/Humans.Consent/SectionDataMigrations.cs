using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Consent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Consent;

internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    internal const string ProfileReviewKey = "consent.profile-review";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(ProfileReviewKey, "Consent",
            "Copy each Consent Coordinator review (consent check, approval, rejection) from profiles into consent_reviews, and record a legacy rejection as users.State = Rejected where the state never did",
            "ConsentReviewService's legacy fallback, UserInfo.IsRejected's legacy arm, the review fields on ProfileInfo and LegacyConsentCheckStatus; then a drop PR for the profiles review columns",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Copy row", "Set Rejected state", "Status", "Approved", "Checked", "Rejected"],
            [.. (await sp.GetRequiredService<ConsentReviewService>().GetUncopiedLegacyReviewsAsync(ct))
                .Select(p => (IReadOnlyList<string>)[
                    p.Review.UserId.ToString(),
                    p.CopyRow ? "yes" : "no",
                    p.SetRejectedState ? "yes" : "no",
                    p.Review.Status?.ToString() ?? string.Empty,
                    p.Review.IsApproved ? "yes" : "no",
                    p.Review.CheckedAt.ToIso8601() ?? string.Empty,
                    p.Review.RejectedAt.ToIso8601() ?? string.Empty])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<ConsentReviewService>().CopyLegacyReviewsAsync(ct);
}
