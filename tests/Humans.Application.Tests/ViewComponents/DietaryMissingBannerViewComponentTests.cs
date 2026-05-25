using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.ViewComponents;

/// <summary>
/// Covers the visibility gate inside <see cref="DietaryMissingBannerViewComponent"/>.
/// Spec: docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md
/// </summary>
public class DietaryMissingBannerViewComponentTests
{
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IUserServiceRead _userRead = Substitute.For<IUserServiceRead>();
    private readonly DietaryMissingBannerViewComponent _sut;

    public DietaryMissingBannerViewComponentTests()
    {
        _sut = new DietaryMissingBannerViewComponent(
            _shiftMgmt,
            _userRead,
            NullLogger<DietaryMissingBannerViewComponent>.Instance);
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

    [HumansFact]
    public async Task Renders_WhenQualifyingSignupAndDietaryEmpty()
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId).Returns(true);
        _userRead.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(UserInfoWith(userId, null));

        var result = await _sut.InvokeAsync(userId);

        result.Should().BeOfType<ViewViewComponentResult>();
    }

    [HumansTheory]
    [InlineData(true, "Vegan")]   // dietary filled
    [InlineData(false, null)]      // no qualifying signup
    [InlineData(false, "Vegan")]   // neither
    public async Task DoesNotRender_WhenGateMissed(bool hasQualifying, string? dietary)
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId).Returns(hasQualifying);
        _userRead.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(UserInfoWith(userId, dietary));

        var result = await _sut.InvokeAsync(userId);

        result.Should().BeOfType<ContentViewComponentResult>()
              .Which.Content.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DoesNotRender_WhenServiceThrows()
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId)
                  .Returns<Task<bool>>(_ => throw new InvalidOperationException("transient DB error"));

        var result = await _sut.InvokeAsync(userId);

        result.Should().BeOfType<ContentViewComponentResult>()
              .Which.Content.Should().BeEmpty();
    }
}
