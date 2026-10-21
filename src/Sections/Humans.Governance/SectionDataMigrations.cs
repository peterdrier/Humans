using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Governance.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Governance;

/// <summary>
/// Governance's data migrations, run from <c>/Debug/DataMigrations</c>. The copy resolves
/// Governance's own service; the Debug host lists, applies and audits the run.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    internal const string ProfileTierKey = "governance.profile-tier";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(ProfileTierKey, "Governance",
            "Copy each Colaborador/Asociado tier from profiles.MembershipTier into governance_membership_tiers",
            "MembershipTierService's legacy fallback, ProfileInfo.MembershipTier and LegacyMembershipTier; then a drop PR for profiles.MembershipTier",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Tier", "Granted", "Term ends", "Granting application"],
            [.. (await sp.GetRequiredService<MembershipTierService>().GetUncopiedLegacyTiersAsync(ct))
                .Select(r => (IReadOnlyList<string>)[
                    r.UserId.ToString(),
                    r.Tier.ToString(),
                    r.GrantedAt.ToIso8601(),
                    r.ExpiresAt.ToInvariantDate() ?? string.Empty,
                    r.GrantedByApplicationId?.ToString() ?? string.Empty])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<MembershipTierService>().CopyLegacyTiersAsync(ct);
}
