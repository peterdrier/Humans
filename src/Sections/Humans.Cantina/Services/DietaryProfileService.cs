using Humans.Cantina.Contracts;
using Humans.Cantina.Data;
using Humans.Cantina.Domain;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Cantina.Services;

/// <summary>
/// The dietary profiles Cantina owns (<c>cantina_dietary_profiles</c>): the reads Shifts, Users and
/// the roster make, the member's own save, the Article 15/17 slice and the account-merge fold.
/// Until <c>cantina.profile-dietary</c> has run, a member without a row reads their legacy
/// <c>profiles</c> food columns through <see cref="UserInfo"/> (Users decomposition plan §3.1 step 4).
/// Never reads medical conditions.
/// </summary>
internal sealed class DietaryProfileService(
    IDietaryProfileRepository repo,
    IUserService users,
    IClock clock) : IDietaryProfileManagement, IUserDataContributor, IUserMerge
{
    internal const string DietaryProfileSlice = "DietaryProfile";

    public async Task<DietaryProfileInfo?> GetDietaryProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var resolvedId = info?.Id ?? userId;
        var row = await repo.GetAsync(resolvedId, ct);
        return row is not null ? ToInfo(row) : LegacyProfile(info);
    }

    public async Task<IReadOnlyDictionary<Guid, DietaryProfileInfo>> GetDietaryProfilesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, DietaryProfileInfo>();
        if (userIds.Count == 0) return result;

        // Keyed by the requested id, read through the merge chain, as IUserService.GetUserInfosAsync does.
        var infos = await users.GetUserInfosAsync(userIds, ct);
        var resolvedIds = userIds.Select(id => infos.TryGetValue(id, out var i) ? i.Id : id).Distinct().ToList();
        var rows = (await repo.GetManyAsync(resolvedIds, ct)).ToDictionary(r => r.UserId);
        foreach (var id in userIds.Distinct())
        {
            infos.TryGetValue(id, out var info);
            var profile = rows.TryGetValue(info?.Id ?? id, out var row) ? ToInfo(row) : LegacyProfile(info);
            if (profile is not null) result[id] = profile;
        }
        return result;
    }

    public async Task<string?> SaveAsync(Guid userId, DietaryProfileInfo answers, CancellationToken ct = default)
    {
        // "Other" requires accompanying free text. The controller checks first, field-targeted;
        // this is the backstop for any other caller.
        if (answers.Allergies.Contains(DietaryOptions.OtherOption, StringComparer.Ordinal)
            && string.IsNullOrWhiteSpace(answers.AllergyOtherText))
            return "Cantina_Dietary_AllergyOther_Required";
        if (answers.Intolerances.Contains(DietaryOptions.OtherOption, StringComparer.Ordinal)
            && string.IsNullOrWhiteSpace(answers.IntoleranceOtherText))
            return "Cantina_Dietary_IntoleranceOther_Required";

        var allergies = answers.Allergies.Where(a => DietaryOptions.AllergyOptions.Contains(a, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        var intolerances = answers.Intolerances.Where(i => DietaryOptions.IntoleranceOptions.Contains(i, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        await repo.UpsertAsync(new DietaryProfile
        {
            UserId = userId,
            // Not re-validated against DietaryOptions.DietaryPreferences: the radio group constrains
            // the UI and the column has always taken any non-blank string (Cantina.md, health.md §5).
            DietaryPreference = string.IsNullOrWhiteSpace(answers.DietaryPreference) ? null : answers.DietaryPreference,
            Allergies = allergies,
            AllergyOtherText = allergies.Contains(DietaryOptions.OtherOption) ? answers.AllergyOtherText?.Trim() : null,
            Intolerances = intolerances,
            IntoleranceOtherText = intolerances.Contains(DietaryOptions.OtherOption) ? answers.IntoleranceOtherText?.Trim() : null,
            UpdatedAt = clock.GetCurrentInstant(),
        }, ct);
        return null;
    }

    // ─── cantina.profile-dietary ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<DietaryProfileInfo>> GetUncopiedLegacyProfilesAsync(CancellationToken ct = default)
    {
        var copied = (await repo.GetAllAsync(ct)).Select(r => r.UserId).ToHashSet();
        return [.. (await users.GetAllUserInfosAsync(ct))
            .Where(u => !copied.Contains(u.Id))
            .OrderBy(u => u.CreatedAt)
            .Select(LegacyProfile)
            .OfType<DietaryProfileInfo>()];
    }

    public async Task<int> CopyLegacyProfilesAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var written = 0;
        foreach (var legacy in await GetUncopiedLegacyProfilesAsync(ct))
        {
            if (await repo.TryInsertAsync(ToEntity(legacy, now), ct))
                written++;
        }
        return written;
    }

    /// <summary>The one reader of the obsolete legacy fields: the read fallback, the copy and the
    /// merge-survivor check. Deleted with the migration once production reports it done. Null when
    /// the member has no food answer on their profile.</summary>
    private static DietaryProfileInfo? LegacyProfile(UserInfo? info)
    {
        if (info?.Profile is not { } p) return null;
#pragma warning disable HUM_PROFILE_DIETARY // the fallback and copy the fields are kept for (plan §3.1)
        var legacy = new DietaryProfileInfo(
            info.Id,
            string.IsNullOrWhiteSpace(p.DietaryPreference) ? null : p.DietaryPreference,
            p.Allergies,
            string.IsNullOrWhiteSpace(p.AllergyOtherText) ? null : p.AllergyOtherText,
            p.Intolerances,
            string.IsNullOrWhiteSpace(p.IntoleranceOtherText) ? null : p.IntoleranceOtherText);
#pragma warning restore HUM_PROFILE_DIETARY
        var empty = legacy.DietaryPreference is null && legacy.Allergies.Count == 0 && legacy.AllergyOtherText is null
                    && legacy.Intolerances.Count == 0 && legacy.IntoleranceOtherText is null;
        return empty ? null : legacy;
    }

    private static DietaryProfileInfo ToInfo(DietaryProfile row) => new(
        row.UserId, row.DietaryPreference, row.Allergies, row.AllergyOtherText, row.Intolerances, row.IntoleranceOtherText);

    private static DietaryProfile ToEntity(DietaryProfileInfo info, Instant now) => new()
    {
        UserId = info.UserId,
        DietaryPreference = info.DietaryPreference,
        Allergies = [.. info.Allergies],
        AllergyOtherText = info.AllergyOtherText,
        Intolerances = [.. info.Intolerances],
        IntoleranceOtherText = info.IntoleranceOtherText,
        UpdatedAt = now,
    };

    // ─── IUserMerge ──────────────────────────────────────────────────────────────

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        _ = actorUserId;
        // The survivor's answers win. Answers still only on its legacy profile count as its own, so
        // they are copied first rather than overwritten by the source's row.
        if (await repo.GetAsync(mergedToUserId, ct) is null
            && LegacyProfile(await users.GetRawUserInfoAsync(mergedToUserId, ct)) is { } legacy)
        {
            await repo.TryInsertAsync(ToEntity(legacy with { UserId = mergedToUserId }, now), ct);
        }
        await repo.ReassignAsync(mergedFromUserId, mergedToUserId, now, ct);
    }

    // ─── IUserDataContributor ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var profile = await GetDietaryProfileAsync(userId, ct);
        return [new UserDataSlice(DietaryProfileSlice, profile is null ? null : new
        {
            profile.DietaryPreference,
            profile.Allergies,
            profile.AllergyOtherText,
            profile.Intolerances,
            profile.IntoleranceOtherText,
        })];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [DietaryProfileSlice] = null };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Deletes the member's row. The legacy <c>profiles</c> food columns are erased by
    /// Users' profile erasure, which still owns them.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        await repo.DeleteAsync(userId, ct);
}
