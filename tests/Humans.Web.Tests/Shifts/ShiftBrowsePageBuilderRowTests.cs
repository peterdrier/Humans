using AwesomeAssertions;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models.Shifts;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Shifts;

/// <summary>
/// <see cref="ShiftBrowsePageBuilder.BuildRowAsync"/> rebuilds a single shift's
/// display item (plus the caller's signup status) for re-rendering one table row
/// after a signup/cancel toggle, without rebuilding the whole browse page.
/// </summary>
public class ShiftBrowsePageBuilderRowTests
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly IShiftManagementService _shiftManagement = Substitute.For<IShiftManagementService>();
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();

    private static readonly EventSettings Event = new()
    {
        Id = Guid.NewGuid(),
        EventName = "Test Event 2026",
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = new LocalDate(2026, 7, 1),
        BuildStartOffset = -14,
        EventEndOffset = 6,
        StrikeEndOffset = 9,
        IsActive = true,
        CreatedAt = TestNow,
        UpdatedAt = TestNow
    };

    [HumansFact]
    public async Task BuildRowAsync_ReturnsMappedItem_AndCallerSignupStatus()
    {
        var shiftId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var shift = new Shift
        {
            Id = shiftId,
            RotaId = Guid.NewGuid(),
            DayOffset = 1,
            IsAllDay = true,
            StartTime = new LocalTime(8, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = 2,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        var urgent = new UrgentShift(
            shift,
            UrgencyScore: 1.5,
            ConfirmedCount: 3,
            RemainingSlots: 2,
            DepartmentName: "Test Department",
            Signups: [(userId, "Tester", SignupStatus.Confirmed)]);

        _shiftManagement.GetActiveAsync().Returns(Event);
        _shiftManagement.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>())
            .Returns(new List<UrgentShift> { urgent });

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

        var builder = new ShiftBrowsePageBuilder(_shiftManagement, _teamService);

        var result = await builder.BuildRowAsync(
            shiftId, userSignups, isPrivileged: false, CancellationToken.None);

        result.Should().NotBeNull();
        var (item, isSignedUp, status) = result!.Value;
        item.Shift.Id.Should().Be(shiftId);
        item.ConfirmedCount.Should().Be(3);
        isSignedUp.Should().BeTrue();
        status.Should().Be(SignupStatus.Confirmed);
    }
}
