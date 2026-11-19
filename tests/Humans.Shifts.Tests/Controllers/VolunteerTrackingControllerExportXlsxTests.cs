using System.Security.Claims;
using AwesomeAssertions;
using Humans.Shifts.Services.Dtos;
using Humans.AuditLog.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Shifts.Controllers;
using Humans.Shifts.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Text;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Users.Domain;
using Humans.Users.Services;

namespace Humans.Shifts.Tests.Controllers;

public sealed class VolunteerTrackingControllerExportXlsxTests
{
    [HumansFact]
    public async Task ExportXlsx_HappyPath_ReturnsFileContentResult()
    {
        var exportService = Substitute.For<IVolunteerTrackingExportService>();
        var model = new VolunteerExportModel(
            MethodologyBlurb: "M",
            FilterSummary: "F",
            GeneratedAtUtc: Instant.FromUtc(2026, 5, 23, 0, 0),
            GeneratedByName: "Actor",
            Days: [new LocalDate(2026, 7, 7)],
            Groups: [],
            TotalsPerDay: [0],
            SuggestedFileName: "volunteer-tracking-2026-07-07-to-2026-07-07.xlsx");
        exportService
            .BuildAsync(Arg.Any<VolunteerExportRequest>(), Arg.Any<CancellationToken>())
            .Returns(model);

        var sut = BuildController(exportService);

        var result = await sut.ExportXlsx(
            departmentId: null,
            startDate: "ignored-invalid-date",
            endDate: "ignored-invalid-date",
            period: ShiftPeriod.Event,
            subPeriod: null,
            ct: Xunit.TestContext.Current.CancellationToken);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        file.FileDownloadName.Should().Be(model.SuggestedFileName);
        file.FileContents.Should().NotBeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData("invalid", "2026-07-09", false)]
    [Xunit.InlineData("2026-07-09", "invalid", false)]
    [Xunit.InlineData("2026-07-09", null, false)]
    [Xunit.InlineData(null, "2026-07-09", false)]
    [Xunit.InlineData("9999-12-31", "9999-12-31", false)]
    [Xunit.InlineData(null, null, true)]
    public async Task ExportXlsx_RejectsInvalidQueriesBeforeBuildingTheWorkbook(string? start, string? end, bool bindingError)
    {
        var exportService = Substitute.For<IVolunteerTrackingExportService>();
        exportService.BuildAsync(Arg.Any<VolunteerExportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VolunteerExportModel("M", "F", Instant.FromUtc(2026, 5, 23, 0, 0), "Actor",
                [new LocalDate(2026, 7, 9)], [], [0], "export.xlsx"));
        var sut = BuildController(exportService);
        if (bindingError) sut.ModelState.AddModelError("departmentId", "Invalid department id");

        var result = await sut.ExportXlsx(null, start, end, null, ct: Xunit.TestContext.Current.CancellationToken);

        result.Should().BeAssignableTo<BadRequestObjectResult>();
        await exportService.DidNotReceive().BuildAsync(Arg.Any<VolunteerExportRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData("2026-07-10", "2026-07-09", "2026-07-09", "2026-07-10")]
    [Xunit.InlineData("9999-12-30", "9999-12-30", "9999-12-30", "9999-12-30")]
    [Xunit.InlineData("-9998-01-01", "-9998-01-02", "-9998-01-01", "-9998-01-02")]
    public async Task ExportXlsx_PreservesRepresentableCustomRanges(string start, string end, string expectedStart, string expectedEnd)
    {
        var from = LocalDatePattern.Iso.Parse(expectedStart).Value;
        var through = LocalDatePattern.Iso.Parse(expectedEnd).Value;
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        _ = from.AtStartOfDayInZone(zone).ToInstant();
        _ = through.PlusDays(1).AtStartOfDayInZone(zone).ToInstant();
        var exportService = Substitute.For<IVolunteerTrackingExportService>();
        exportService.BuildAsync(Arg.Any<VolunteerExportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VolunteerExportModel("M", "F", Instant.FromUtc(2026, 5, 23, 0, 0), "Actor",
                [new LocalDate(2026, 7, 9)], [], [0], "export.xlsx"));

        var result = await BuildController(exportService).ExportXlsx(null, start, end, null, ct: Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<FileContentResult>();
        await exportService.Received(1).BuildAsync(Arg.Is<VolunteerExportRequest>(r => r.StartDate == from && r.EndDate == through),
            Xunit.TestContext.Current.CancellationToken);
    }

    [HumansFact]
    public async Task ExportXlsx_BuildPeriodWithSetupWeekSubPeriod_NarrowsRangeToSubPeriodBounds()
    {
        var exportService = Substitute.For<IVolunteerTrackingExportService>();
        VolunteerExportRequest? captured = null;
        var model = new VolunteerExportModel(
            MethodologyBlurb: "M",
            FilterSummary: "F",
            GeneratedAtUtc: Instant.FromUtc(2026, 5, 23, 0, 0),
            GeneratedByName: "Actor",
            Days: [new LocalDate(2026, 6, 23)],
            Groups: [],
            TotalsPerDay: [0],
            SuggestedFileName: "volunteer-tracking-setup-week.xlsx");
        exportService
            .BuildAsync(Arg.Do<VolunteerExportRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(model);

        var sut = BuildController(exportService);

        var result = await sut.ExportXlsx(
            departmentId: null,
            startDate: null,
            endDate: null,
            period: ShiftPeriod.Build,
            subPeriod: BuildSubPeriod.SetupWeek,
            ct: Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<FileContentResult>();
        captured.Should().NotBeNull();

        // BuildController seeds GateOpeningDate=2026-07-09 with the EventSettings
        // defaults for sub-period offsets (SetupWeek=-16, PreEventWeek=-9). So
        // SetupWeek bounds → [Gate-16, Gate-10] inclusive (end is exclusive-1).
        var gate = new LocalDate(2026, 7, 9);
        captured!.StartDate.Should().Be(gate.PlusDays(-16));
        captured.EndDate.Should().Be(gate.PlusDays(-10));
    }

    private static VolunteerTrackingController BuildController(IVolunteerTrackingExportService exportService)
    {
        var service = Substitute.For<IVolunteerTrackingManagement>();
        var shiftMgmt = Substitute.For<IShiftManagement>();
        var userService = Substitute.For<IUserService>();
        var localizer = Substitute.For<IStringLocalizer<ShiftsResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        var xlsxBuilder = new VolunteerTrackingXlsxBuilder();

        var currentUserId = Guid.NewGuid();
        var currentUser = new User
        {
            Id = currentUserId,
            DisplayName = "Actor",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = currentUserId,
            BurnerName = "Actor",
            FirstName = "Actor",
            LastName = "Test",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        userService.GetUserInfoAsync(currentUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFactory.Create(
                user: currentUser,
                userEmails: [],
                externalLogins: [],
                profile: profile,
                contactFields: [],
                profileLanguages: [],
                volunteerHistory: [])));

        // Sub-period offsets mirror the EventSettings entity defaults
        // (FirstCrew=-25, SetupWeek=-16, PreEventWeek=-9, FinishingWeekend=-4),
        // which the SetupWeek bounds assertion above depends on.
        var activeEvent = new BurnSettingsInfo(
            Id: Guid.NewGuid(),
            EventName: "Test Burn",
            Year: 2026,
            TimeZoneId: "Europe/Madrid",
            GateOpeningDate: new LocalDate(2026, 7, 9),
            BuildStartOffset: -7,
            EventEndOffset: 4,
            StrikeEndOffset: 6,
            FirstCrewStartOffset: -25,
            SetupWeekStartOffset: -16,
            PreEventWeekStartOffset: -9,
            FinishingWeekendStartOffset: -4,
            EarlyEntryCapacity: new Dictionary<int, int>(),
            BarriosEarlyEntryAllocation: null,
            EarlyEntryClose: null,
            IsShiftBrowsingOpen: false);
        var burnSettings = Substitute.For<IBurnSettingsService>();
        burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(activeEvent);

        var ctrl = new VolunteerTrackingController(
            service, shiftMgmt, burnSettings, exportService, xlsxBuilder,
            userService, localizer, NullLogger<VolunteerTrackingController>.Instance)
        {
            ControllerContext = BuildControllerContext(currentUserId),
        };
        ctrl.TempData = new TempDataDictionary(
            ctrl.ControllerContext.HttpContext,
            Substitute.For<ITempDataProvider>());
        return ctrl;
    }

    private static ControllerContext BuildControllerContext(Guid currentUserId)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString())],
            "test"));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        http.RequestServices = services.BuildServiceProvider();

        return new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor
            {
                ActionName = nameof(VolunteerTrackingController.ExportXlsx),
            },
        };
    }
}
