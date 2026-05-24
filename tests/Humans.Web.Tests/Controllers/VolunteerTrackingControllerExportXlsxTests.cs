using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Humans.Web.Models.VolunteerTracking;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Verifies the <see cref="VolunteerTrackingController.ExportXlsx"/> action wires
/// service + builder + active-event lookup correctly: returns an XLSX
/// <see cref="FileContentResult"/> with the builder's suggested filename and
/// the canonical spreadsheet content-type.
/// </summary>
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
            startDate: null,
            endDate: null,
            period: ShiftPeriod.Event,
            ct: default);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        file.FileDownloadName.Should().Be(model.SuggestedFileName);
        file.FileContents.Should().NotBeEmpty();
    }

    private static VolunteerTrackingController BuildController(IVolunteerTrackingExportService exportService)
    {
        var service = Substitute.For<IVolunteerTrackingService>();
        var shiftMgmt = Substitute.For<IShiftManagementService>();
        var userService = Substitute.For<IUserService>();
        var auditLog = Substitute.For<IAuditLogService>();
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        var xlsxBuilder = new VolunteerTrackingXlsxBuilder();

        var currentUserId = Guid.NewGuid();
        var currentUser = new User
        {
            Id = currentUserId,
            DisplayName = "Actor",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        };
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = currentUserId,
            BurnerName = "Actor",
            FirstName = "Actor",
            LastName = "Test",
            IsApproved = true,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        userService.GetUserInfoAsync(currentUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                user: currentUser,
                userEmails: [],
                eventParticipations: [],
                externalLogins: [],
                profile: profile,
                contactFields: [],
                profileLanguages: [],
                volunteerHistory: [],
                communicationPreferences: [])));

        var activeEvent = new EventSettings
        {
            Id = Guid.NewGuid(),
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 9),
            BuildStartOffset = -7,
            EventEndOffset = 4,
            StrikeEndOffset = 6,
        };
        shiftMgmt.GetActiveAsync().Returns(activeEvent);

        var ctrl = new VolunteerTrackingController(
            service, shiftMgmt, exportService, xlsxBuilder,
            userService, auditLog, localizer)
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
