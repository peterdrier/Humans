using System.Security.Claims;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Settings.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Settings.Tests;

/// <summary>
/// The /Settings#event tab (peterdrier/Humans#1628) never hides for a non-admin, but a
/// non-admin must get the read-only model — no form to POST from.
/// </summary>
public sealed class EventSettingsTabViewComponentTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    private static EventSettingsInfo MakeInfo() => new(
        Id: Guid.NewGuid(),
        EventName: "Nowhere 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 7, 9),
        BuildStartOffset: -25,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -25,
        SetupWeekStartOffset: -16,
        PreEventWeekStartOffset: -9,
        FinishingWeekendStartOffset: -4,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        Status: EventSettingsStatus.Active);

    private async Task<EventSettingsTabViewModel> InvokeAsAsync(
        bool authorized, string? eventQuery = null, EventSettingsInfo? active = null)
    {
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(active ?? MakeInfo());
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>())
            .Returns(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        var httpContext = new DefaultHttpContext();
        if (eventQuery is not null)
            httpContext.Request.QueryString = new QueryString($"?event={eventQuery}");

        var sut = new EventSettingsTabViewComponent(_settings, _authorization)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext },
            },
        };

        var result = await sut.InvokeAsync();
        return (EventSettingsTabViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
    }

    [HumansFact]
    public async Task InvokeAsync_NonAdmin_GetsTheReadOnlyModel()
    {
        var model = await InvokeAsAsync(authorized: false);

        model.CanEdit.Should().BeFalse();
        model.Settings.Should().NotBeNull("the read-only view still needs the values to display");
    }

    [HumansFact]
    public async Task InvokeAsync_Admin_GetsTheEditableModel()
    {
        var model = await InvokeAsAsync(authorized: true);

        model.CanEdit.Should().BeTrue();
    }

    [HumansFact]
    public async Task InvokeAsync_Admin_WithEventQuery_ReturnsTheNamedRow()
    {
        var namedId = Guid.NewGuid();
        var namedRow = MakeInfo() with { Id = namedId, EventName = "Named row" };
        _settings.GetEventSettingsByIdAsync(namedId, Arg.Any<CancellationToken>()).Returns(namedRow);

        var model = await InvokeAsAsync(authorized: true, eventQuery: namedId.ToString());

        model.Settings!.Id.Should().Be(namedId);
        await _settings.DidNotReceive().GetActiveEventSettingsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvokeAsync_NonAdmin_WithEventQuery_StillGetsTheActiveRow()
    {
        // A non-admin must never see anything other than the active row, even if
        // they carry the same ?event= a link gave an admin.
        var namedId = Guid.NewGuid();
        var active = MakeInfo();
        _settings.GetEventSettingsByIdAsync(namedId, Arg.Any<CancellationToken>())
            .Returns(MakeInfo() with { Id = namedId, EventName = "Named row" });

        var model = await InvokeAsAsync(authorized: false, eventQuery: namedId.ToString(), active: active);

        model.Settings!.Id.Should().Be(active.Id);
        await _settings.DidNotReceive().GetEventSettingsByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvokeAsync_Admin_WithEventQueryNamingAMissingRow_FallsBackToTheActiveRow()
    {
        var missingId = Guid.NewGuid();
        var active = MakeInfo();
        _settings.GetEventSettingsByIdAsync(missingId, Arg.Any<CancellationToken>())
            .Returns((EventSettingsInfo?)null);

        var model = await InvokeAsAsync(authorized: true, eventQuery: missingId.ToString(), active: active);

        model.Settings!.Id.Should().Be(active.Id);
    }

    [HumansFact]
    public async Task InvokeAsync_NoActiveEvent_ReturnsANullSettingsModelRegardlessOfPolicy()
    {
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettingsInfo?)null);
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var sut = new EventSettingsTabViewComponent(_settings, _authorization)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };

        var result = await sut.InvokeAsync();
        var model = (EventSettingsTabViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.Settings.Should().BeNull();
    }
}
