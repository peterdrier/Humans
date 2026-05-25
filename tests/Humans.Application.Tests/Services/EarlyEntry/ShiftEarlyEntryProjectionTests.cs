using AwesomeAssertions;
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Services.Shifts;
using NodaTime;
using NodaTime.Text;

namespace Humans.Application.Tests.Services.EarlyEntry;

public class ShiftEarlyEntryProjectionTests
{
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private static Instant At(int y, int m, int d, int h) =>
        Madrid.AtLeniently(new LocalDateTime(y, m, d, h, 0)).ToInstant();

    [HumansFact]
    public void Earliest_shift_drives_grant_entry_date_minus_one_and_source_from_that_team()
    {
        var userId = Guid.NewGuid();
        var flagsTeam = Guid.NewGuid();
        var powerTeam = Guid.NewGuid();

        // Wed 2026-07-01 shift on Flags (earliest), Fri 2026-07-03 shift on Power
        var rows = new List<ConfirmedShiftRow>
        {
            new(userId, flagsTeam, At(2026, 7, 1, 9), At(2026, 7, 1, 17)),
            new(userId, powerTeam, At(2026, 7, 3, 9), At(2026, 7, 3, 17)),
        };

        var teamNames = new Dictionary<Guid, string>
        {
            [flagsTeam] = "Flags",
            [powerTeam] = "Power",
        };

        var grants = ShiftEarlyEntryProjection.Project(rows, Madrid, teamNames);

        grants.Should().ContainSingle();
        var grant = grants[0];
        grant.UserId.Should().Be(userId);
        grant.EntryDate.Should().Be(new LocalDate(2026, 6, 30)); // 2026-07-01 - 1
        grant.Source.Should().Be("Shift: Flags");
    }

    [HumansFact]
    public void Same_earliest_day_breaks_tie_on_team_name_ascending()
    {
        var userId = Guid.NewGuid();
        var zebraTeam = Guid.NewGuid();
        var alphaTeam = Guid.NewGuid();

        // Two shifts on the SAME earliest day, Zebra listed first.
        var rows = new List<ConfirmedShiftRow>
        {
            new(userId, zebraTeam, At(2026, 7, 1, 9), At(2026, 7, 1, 17)),
            new(userId, alphaTeam, At(2026, 7, 1, 18), At(2026, 7, 1, 22)),
        };

        var teamNames = new Dictionary<Guid, string>
        {
            [zebraTeam] = "Zebra",
            [alphaTeam] = "Alpha",
        };

        var grants = ShiftEarlyEntryProjection.Project(rows, Madrid, teamNames);

        grants.Should().ContainSingle();
        grants[0].Source.Should().Be("Shift: Alpha");
    }

    [HumansFact]
    public void Empty_rows_produces_empty_result()
    {
        var grants = ShiftEarlyEntryProjection.Project(
            [],
            Madrid,
            new Dictionary<Guid, string>());

        grants.Should().BeEmpty();
    }
}
