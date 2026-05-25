using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using Humans.Web;
using Humans.Web.Models;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Application.Tests.ViewComponents;

/// <summary>
/// Covers the dietary-medical nudge gate in <see cref="ThingsToDoViewComponent"/>.
/// Spec: docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md
/// </summary>
public class ThingsToDoViewComponentDietaryGateTests
{
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly IStringLocalizer<SharedResource> _localizer = Substitute.For<IStringLocalizer<SharedResource>>();
    private readonly ThingsToDoViewComponent _sut;

    public ThingsToDoViewComponentDietaryGateTests()
    {
        // Each [key] returns a LocalizedString whose Value == key. Lets tests
        // assert against the key name (no resx lookup needed).
        _localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        // Default profile snapshot — empty consents so the consent item is
        // skipped (RequiredConsentCount==0), keeping the dashboard items list
        // focused on what these tests care about.
        _membershipCalculator.GetMembershipSnapshotAsync(Arg.Any<Guid>())
            .Returns(new MembershipSnapshot(
                Status: MembershipStatus.Active,
                IsVolunteerMember: true,
                RequiredConsentCount: 0,
                PendingConsentCount: 0,
                MissingConsentVersionIds: Array.Empty<Guid>()));

        _sut = new ThingsToDoViewComponent(
            _userService,
            _shiftMgmt,
            _membershipCalculator,
            _localizer,
            NullLogger<ThingsToDoViewComponent>.Instance);

        // Url helper — the component calls Url.Action(...) for the dietary item.
        // Return a stable href so the test doesn't care about real routing.
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.Action(Arg.Any<UrlActionContext>()).Returns("/Profile/DietaryMedical");
        _sut.Url = urlHelper;

        // ViewContext is required for ViewComponent.View(model) → ViewViewComponentResult
        _sut.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(),
        };
    }

    [HumansFact]
    public async Task DietaryItemAppearsWithNoShiftCopyWhenNoQualifyingSignup()
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId).Returns(false);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(UserInfoWith(userId, null));

        var result = await _sut.InvokeAsync(userId, isVolunteerMember: true, hasShiftSignups: false, profileCompletionPercent: 100);

        var model = (ThingsToDoViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
        var dietary = model.Items.Should().ContainSingle(i => i.Key == "dietary-medical").Subject;
        dietary.IsDone.Should().BeFalse();
        dietary.Description.Should().Be(_localizer["Todo_DietaryMedical_NoShift_Pending"].Value);
    }

    [HumansFact]
    public async Task DietaryItemUsesExistingCopyWhenHasQualifyingSignup()
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId).Returns(true);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(UserInfoWith(userId, null));

        var result = await _sut.InvokeAsync(userId, isVolunteerMember: true, hasShiftSignups: true, profileCompletionPercent: 100);

        var model = (ThingsToDoViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
        var dietary = model.Items.Should().ContainSingle(i => i.Key == "dietary-medical").Subject;
        dietary.Description.Should().Be(_localizer["Todo_DietaryMedical_Pending"].Value);
    }

    [HumansFact]
    public async Task DietaryItemNotAddedWhenDietaryFilled()
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId).Returns(false);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(UserInfoWith(userId, "Vegetarian"));

        var result = await _sut.InvokeAsync(userId, isVolunteerMember: true, hasShiftSignups: false, profileCompletionPercent: 100);

        if (result is ViewViewComponentResult viewResult)
        {
            var model = (ThingsToDoViewModel?)viewResult.ViewData!.Model;
            model?.Items.Should().NotContain(i => i.Key == "dietary-medical");
        }
        // ContentResult (empty) is also valid — means the card hid entirely because all items are Done.
    }

    private static UserInfo UserInfoWith(Guid userId, string? dietary) => UserInfo.Create(
        user: new User { Id = userId, DisplayName = "Test", PreferredLanguage = "en" },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: new Profile { UserId = userId, BurnerName = "Test", DietaryPreference = dietary },
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);
}
