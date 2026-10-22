using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Users.Contracts;
using NodaTime;
using MemberApplication = Humans.Governance.Domain.Application;

namespace Humans.Governance.Services;

/// <summary>
/// Until the <c>governance.profile-tier</c> copy has run, a member without a row reads their
/// legacy <c>profiles.MembershipTier</c> through <see cref="UserInfo"/>.
/// </summary>
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
        return row?.Tier ?? LegacyTier(info);
    }

    /// <summary>Every member whose tier is not Volunteer, keyed by user id.</summary>
    public async Task<IReadOnlyDictionary<Guid, MembershipTier>> GetTiersAsync(CancellationToken ct = default)
    {
        var rows = (await repo.GetAllAsync(ct)).ToDictionary(r => r.UserId);
        var result = new Dictionary<Guid, MembershipTier>();
        foreach (var info in await users.GetAllUserInfosAsync(ct))
        {
            if (!rows.ContainsKey(info.Id) && LegacyTier(info) is var legacy and not MembershipTier.Volunteer)
                result[info.Id] = legacy;
        }
        foreach (var row in rows.Values.Where(r => r.Tier != MembershipTier.Volunteer))
            result[row.UserId] = row.Tier;
        return result;
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

    /// <summary>Living members whose legacy profile tier is not Volunteer and who have no row yet,
    /// with the row the copy would write (term and grant read from their latest Approved
    /// application for that tier, when there is one).</summary>
    public async Task<IReadOnlyList<MembershipTierRecord>> GetUncopiedLegacyTiersAsync(CancellationToken ct = default)
    {
        var copied = (await repo.GetAllAsync(ct)).Select(r => r.UserId).ToHashSet();
        var pending = new List<MembershipTierRecord>();
        foreach (var info in (await users.GetAllUserInfosAsync(ct)).OrderBy(u => u.CreatedAt))
        {
            if (copied.Contains(info.Id) || LegacyTier(info) is not (var tier and not MembershipTier.Volunteer))
                continue;
            pending.Add(await LegacyRecordAsync(info.Id, tier, ct));
        }
        return pending;
    }

    public async Task<int> CopyLegacyTiersAsync(CancellationToken ct = default)
    {
        var written = 0;
        foreach (var record in await GetUncopiedLegacyTiersAsync(ct))
        {
            if (await repo.TryInsertAsync(record, ct))
                written++;
        }
        return written;
    }

    private async Task<MembershipTierRecord> LegacyRecordAsync(Guid userId, MembershipTier tier, CancellationToken ct)
    {
        var granting = LatestApproved(await applications.GetByUserIdAsync(userId, ct), tier);
        return new MembershipTierRecord
        {
            UserId = userId,
            Tier = tier,
            GrantedAt = granting?.ResolvedAt ?? clock.GetCurrentInstant(),
            ExpiresAt = granting?.TermExpiresAt,
            GrantedByApplicationId = granting?.Id,
        };
    }

    private static MemberApplication? LatestApproved(IEnumerable<MemberApplication> apps, MembershipTier tier) =>
        apps.Where(a => a.Status == ApplicationStatus.Approved && a.MembershipTier == tier)
            .MaxBy(a => a.ResolvedAt);

    /// <summary>The one reader of the obsolete legacy field: the read fallback, the copy and the
    /// merge-survivor check. Deleted with the migration once production reports it done.
    /// The legacy enum maps by name (same members, same stored strings).</summary>
    private static MembershipTier LegacyTier(UserInfo? info)
    {
#pragma warning disable HUM_PROFILE_TIER // the fallback and copy the field is kept for (plan §3.1)
        var legacy = info?.Profile?.MembershipTier ?? LegacyMembershipTier.Volunteer;
#pragma warning restore HUM_PROFILE_TIER
        return legacy switch
        {
            LegacyMembershipTier.Colaborador => MembershipTier.Colaborador,
            LegacyMembershipTier.Asociado => MembershipTier.Asociado,
            _ => MembershipTier.Volunteer,
        };
    }

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        _ = actorUserId;
        _ = now;
        // The survivor's tier wins. A tier still only on its legacy profile counts as its own, so
        // it is copied first rather than overwritten by the source's row.
        if (await repo.GetAsync(mergedToUserId, ct) is null
            && LegacyTier(await users.GetRawUserInfoAsync(mergedToUserId, ct)) is var legacy and not MembershipTier.Volunteer)
        {
            await repo.TryInsertAsync(await LegacyRecordAsync(mergedToUserId, legacy, ct), ct);
        }
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
            : LegacyTier(info) is var legacy and not MembershipTier.Volunteer ? new { Tier = legacy } : null;
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
