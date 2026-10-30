using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Users.Contracts;
using NodaTime;
using MemberApplication = Humans.Governance.Domain.Application;

namespace Humans.Governance.Services;

internal sealed class MembershipTierService(
    IMembershipTierRepository repo,
    IApplicationRepository applications,
    IUserService users,
    IClock clock) : IUserDataContributor, IUserMerge
{
    internal const string MembershipTierSlice = "MembershipTier";

    public async Task<MembershipTier> GetTierAsync(Guid userId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var row = await repo.GetAsync(info?.Id ?? userId, ct);
        return row?.Tier ?? MembershipTier.Volunteer;
    }

    /// <summary>Every member whose tier is not Volunteer, keyed by user id.</summary>
    public async Task<IReadOnlyDictionary<Guid, MembershipTier>> GetTiersAsync(CancellationToken ct = default)
    {
        return (await repo.GetAllAsync(ct))
            .Where(r => r.Tier != MembershipTier.Volunteer)
            .ToDictionary(r => r.UserId, r => r.Tier);
    }

    /// <summary>Approval: the member now holds <paramref name="application"/>'s tier until its term ends.</summary>
    public Task GrantAsync(MemberApplication application, CancellationToken ct = default) =>
        repo.UpsertAsync(new MembershipTierRecord
        {
            UserId = application.UserId,
            Tier = application.MembershipTier,
            GrantedAt = clock.GetCurrentInstant(),
            ExpiresAt = application.TermExpiresAt,
            GrantedByApplicationId = application.Id,
        }, ct);

    /// <summary>Keeps <c>ExpiresAt</c> true when an admin corrects the granting application's term.</summary>
    public Task SyncExpiryAsync(Guid applicationId, LocalDate? expiresAt, CancellationToken ct = default) =>
        repo.UpdateExpiryForApplicationAsync(applicationId, expiresAt, ct);

    public async Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>> ExpireAsync(
        MembershipTier tier, LocalDate today, CancellationToken ct = default)
    {
        var inTerm = (await applications.GetActiveApprovedTierUserIdsAsync(tier, today, ct)).ToHashSet();
        var otherTierByUser = await applications.GetOtherActiveTierAssignmentsAsync(tier, today, ct);
        var now = clock.GetCurrentInstant();

        var result = new List<(Guid UserId, MembershipTier NewTier)>();
        foreach (var (userId, current) in await GetTiersAsync(ct))
        {
            if (current != tier || inTerm.Contains(userId))
                continue;

            var newTier = otherTierByUser.GetValueOrDefault(userId, MembershipTier.Volunteer);
            var granting = newTier == MembershipTier.Volunteer
                ? null
                : LatestApproved(await applications.GetByUserIdAsync(userId, ct), newTier);
            await repo.UpsertAsync(new MembershipTierRecord
            {
                UserId = userId,
                Tier = newTier,
                GrantedAt = now,
                ExpiresAt = granting?.TermExpiresAt,
                GrantedByApplicationId = granting?.Id,
            }, ct);
            result.Add((userId, newTier));
        }
        return result;
    }

    private static MemberApplication? LatestApproved(IEnumerable<MemberApplication> apps, MembershipTier tier) =>
        apps.Where(a => a.Status == ApplicationStatus.Approved && a.MembershipTier == tier)
            .MaxBy(a => a.ResolvedAt);

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        _ = actorUserId;
        _ = now;
        await repo.ReassignAsync(mergedFromUserId, mergedToUserId, ct);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var row = await repo.GetAsync(info?.Id ?? userId, ct);
        object? data = row is not null
            ? new
            {
                row.Tier,
                GrantedAt = row.GrantedAt.ToIso8601(),
                ExpiresAt = row.ExpiresAt.ToInvariantDate(),
                row.GrantedByApplicationId,
            }
            : null;
        return [new UserDataSlice(MembershipTierSlice, data)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MembershipTierSlice] =
                "Retained: the member's tier, its grant date, term end and granting application are " +
                "the association's register of members (Ley Orgánica 1/2002 Art. 14; GDPR Art. 17(3)(b)). " +
                "The row holds no free text.",
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Nothing to erase: the whole slice is retained (see <see cref="ErasureDeclaration"/>).</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
