using Humans.Base.Interfaces;
using Humans.Cantina.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Cantina;

/// <summary>
/// Cantina's data migrations, run from <c>/Debug/DataMigrations</c>. The copy resolves Cantina's
/// own service; the Debug host lists, applies and audits the run.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    internal const string ProfileDietaryKey = "cantina.profile-dietary";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(ProfileDietaryKey, "Cantina",
            "Copy each member's dietary preference, allergies and intolerances from profiles into cantina_dietary_profiles",
            "DietaryProfileService's legacy fallback and the five ProfileInfo food fields; then a drop PR for the profiles food columns",
            StatusAsync, ApplyAsync),
    ];

    // No medical data here: the copy never reads MedicalConditions, which stays in Users.
    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Preference", "Allergies", "Allergy (other)", "Intolerances", "Intolerance (other)"],
            [.. (await sp.GetRequiredService<IDietaryProfileManagement>().GetUncopiedLegacyProfilesAsync(ct))
                .Select(p => (IReadOnlyList<string>)[
                    p.UserId.ToString(),
                    p.DietaryPreference ?? string.Empty,
                    string.Join(", ", p.Allergies),
                    p.AllergyOtherText ?? string.Empty,
                    string.Join(", ", p.Intolerances),
                    p.IntoleranceOtherText ?? string.Empty])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IDietaryProfileManagement>().CopyLegacyProfilesAsync(ct);
}
