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
        return row is null ? null : ToInfo(row);
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
            if (rows.TryGetValue(info?.Id ?? id, out var row)) result[id] = ToInfo(row);
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

    private static DietaryProfileInfo ToInfo(DietaryProfile row) => new(
        row.UserId, row.DietaryPreference, row.Allergies, row.AllergyOtherText, row.Intolerances, row.IntoleranceOtherText);

    // ─── IUserMerge ──────────────────────────────────────────────────────────────

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        _ = actorUserId;
        // The survivor's answers win.
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
    /// Users' profile erasure until they are dropped.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        await repo.DeleteAsync(userId, ct);
}
