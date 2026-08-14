using Humans.Shifts.Domain;
using AwesomeAssertions;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Domain.Enums;
using Humans.Shifts.Models;
using NodaTime;
using NSubstitute;

namespace Humans.Shifts.Tests.Models;

/// <summary>
/// <see cref="ShiftBrowsePageBuilder.BuildRowAsync"/> rebuilds a single shift's
/// display item (plus the caller's signup status) for re-rendering one table row
/// after a signup/cancel toggle, without rebuilding the whole browse page.
/// </summary>
public class ShiftBrowsePageBuilderRowTests
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly IShiftManagementService _shiftManagement = Substitute.For<IShiftManagementService>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();

    private static readonly BurnSettingsInfo Event = new(
        Id: Guid.NewGuid(),
        EventName: "Test Event 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 7, 1),
        BuildStartOffset: -14,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -14,
        SetupWeekStartOffset: -10,
        PreEventWeekStartOffset: -7,
        FinishingWeekendStartOffset: -3,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: true);

    [HumansFact]
    public async Task BuildRowAsync_ReturnsMappedItem_AndCallerSignupStatus()
    {
        var shiftId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var shift = UrgentShiftFixtures.Shift(
            id: shiftId,
            dayOffset: 1,
            isAllDay: true,
            minVolunteers: 2,
            maxVolunteers: 5);
        var urgent = UrgentShiftFixtures.Urgent(
            shift: shift,
            burn: Event,
            urgencyScore: 1.5,
            confirmedCount: 3,
            remainingSlots: 2,
            signups: [new ShiftSignupInfo(userId, "Tester", SignupStatus.Confirmed)]);

        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Event);
        _shiftManagement.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>())
            .Returns([urgent]);

        var userSignups = new List<ShiftSignup>
        {
            new()
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShiftId = shiftId,
                Status = SignupStatus.Confirmed,
                CreatedAt = TestNow,
                UpdatedAt = TestNow
            }
        };

        var builder = new ShiftBrowsePageBuilder(_shiftManagement, _burnSettings, _teamService);

        var result = await builder.BuildRowAsync(
            shiftId, userSignups, isPrivileged: false, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var (item, isSignedUp, status) = result.Value;
        item.Shift.Id.Should().Be(shiftId);
        item.ConfirmedCount.Should().Be(3);
        isSignedUp.Should().BeTrue();
        status.Should().Be(SignupStatus.Confirmed);
    }
}
