using Humans.Application.Csv;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.Models.CampAdmin;

public sealed record CampCsvExport(byte[] Content, string ContentType, string FileName);

public sealed class CampCsvExportBuilder(
    ICampServiceRead campService, IUserServiceRead userService)
{
    public async Task<CampCsvExport> BuildAsync()
    {
        var settings = await campService.GetSettingsAsync();
        var year = settings.PublicYear;
        var camps = await campService.GetCampsForYearAsync(year);

        var leadUserIds = camps
            .SelectMany(camp => camp.Seasons.FirstOrDefault()?.LeadUserIds ?? Array.Empty<Guid>())
            .Distinct()
            .ToList();
        var leadUsers = await userService.GetUserInfosAsync(leadUserIds);

        var bytes = HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow(
                "Name", "Slug", "Status", "Contact Email", "Contact Phone",
                "Leads", "Languages", "Member Count",
                "Space Requirement", "Sound Zone", "Electrical Grid",
                "Accepting Members", "Kids Welcome", "Adult Playspace",
                "Vibes", "Swiss Camp", "Times Participating");

            foreach (var camp in camps)
            {
                var season = camp.Seasons.FirstOrDefault();
                if (season is null) continue;

                var leads = string.Join("; ", season.LeadUserIds
                    .Select(id =>
                    {
                        var user = leadUsers.TryGetValue(id, out var u) ? u : null;
                        return $"{user?.BurnerName ?? string.Empty} <{user?.Email ?? string.Empty}>";
                    }));

                var vibes = season.Vibes.Count > 0
                    ? string.Join(", ", season.Vibes)
                    : "";

                csv.WriteRow(
                    season.Name,
                    camp.Slug,
                    season.Status,
                    camp.ContactEmail,
                    camp.ContactPhone,
                    leads,
                    season.Languages,
                    season.MemberCount,
                    season.SpaceRequirement?.ToString() ?? "",
                    season.SoundZone?.ToString() ?? "",
                    season.ElectricalGrid?.ToString() ?? "",
                    season.AcceptingMembers,
                    season.KidsWelcome,
                    season.AdultPlayspace,
                    vibes,
                    camp.IsSwissCamp ? "Yes" : "No",
                    camp.TimesAtNowhere);
            }
        });

        return new CampCsvExport(bytes, "text/csv", $"barrios-{year}.csv");
    }
}
