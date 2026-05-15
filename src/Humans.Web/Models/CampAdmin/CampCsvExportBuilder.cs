using System.Text;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Users;
using Humans.Web.Extensions;

namespace Humans.Web.Models.CampAdmin;

public sealed record CampCsvExport(byte[] Content, string ContentType, string FileName);

public sealed class CampCsvExportBuilder
{
    private readonly ICampService _campService;
    private readonly IUserService _userService;

    public CampCsvExportBuilder(ICampService campService, IUserService userService)
    {
        _campService = campService;
        _userService = userService;
    }

    public async Task<CampCsvExport> BuildAsync()
    {
        var settings = await _campService.GetSettingsAsync();
        var year = settings.PublicYear;
        var camps = await _campService.GetCampsWithLeadsForYearAsync(year);

        var leadUserIds = camps
            .SelectMany(c => (c.Leads ?? []).Select(l => l.UserId))
            .Distinct()
            .ToList();
        var leadUsers = await _userService.GetByIdsAsync(leadUserIds);

        var csv = new StringBuilder();
        csv.AppendCsvRow(
            "Name", "Slug", "Status", "Contact Email", "Contact Phone",
            "Leads", "Languages", "Member Count",
            "Space Requirement", "Sound Zone", "Electrical Grid",
            "Accepting Members", "Kids Welcome", "Adult Playspace",
            "Vibes", "Swiss Camp", "Times Participating");

        foreach (var camp in camps)
        {
            var season = camp.Seasons.FirstOrDefault();
            if (season is null) continue;

            var leads = string.Join("; ", (camp.Leads ?? [])
                .Select(l =>
                {
                    var user = leadUsers.TryGetValue(l.UserId, out var u) ? u : null;
                    return $"{user?.DisplayName ?? string.Empty} <{user?.Email ?? string.Empty}>";
                }));

            var vibes = season.Vibes.Count > 0
                ? string.Join(", ", season.Vibes)
                : "";

            csv.AppendCsvRow(
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

        return new CampCsvExport(
            Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv",
            $"barrios-{year}.csv");
    }
}
