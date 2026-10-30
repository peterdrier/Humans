using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Authorization;
using Humans.Onboarding;
using Humans.Shifts.Contracts;
using Humans.Shifts.Controllers;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Shifts.Tests.Infrastructure;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Shifts.Tests.Controllers;

/// <summary>
/// The audited medical reveal: the one place Art. 9 text leaves the server for a viewer other than its owner.
/// </summary>
public sealed class ShiftsControllerRevealMedicalTests
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly Guid _subjectId = Guid.NewGuid();

    [HumansFact]
    public void RevealMedical_IsAnAntiforgeryPost_GatedOnMedicalDataViewer()
    {
        var action = typeof(ShiftsController).GetMethod(nameof(ShiftsController.RevealMedical))!;

        action.GetCustomAttribute<HttpPostAttribute>().Should().NotBeNull();
        action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
        action.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be(PolicyNames.MedicalDataViewer);
    }

    [HumansFact]
    public async Task RevealMedical_ReturnsTheText_AndAuditsSubjectAndActor()
    {
        StubSubject("Epilepsy");

        var result = await BuildSut().RevealMedical(_subjectId, CancellationToken.None);

        result.Should().BeOfType<JsonResult>().Which.Value.Should().BeEquivalentTo(new { text = "Epilepsy" });
        await _audit.Received(1).LogAsync(
            ShiftsAuditActions.MedicalConditionsRevealed, "User", _subjectId,
            Arg.Is<string>(d => !d.Contains("Epilepsy", StringComparison.Ordinal)), _actorId);
    }

    [HumansFact]
    public async Task RevealMedical_WithNothingOnFile_IsNotFound_AndAuditsNothing()
    {
        StubSubject(null);

        var result = await BuildSut().RevealMedical(_subjectId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    private void StubSubject(string? medicalConditions) =>
        _userService.GetUserInfoAsync(_subjectId, Arg.Any<CancellationToken>())
            .Returns(UserInfoStubHelpers.MakeUserInfo(_subjectId, profile: new Profile
            {
                Id = Guid.NewGuid(),
                UserId = _subjectId,
                BurnerName = "Ann",
                MedicalConditions = medicalConditions,
                CreatedAt = TestNow,
                UpdatedAt = TestNow,
                IsApproved = true,
            }));

    private ShiftsController BuildSut()
    {
        var shiftMgmt = Substitute.For<IShiftManagement>();
        var burnSettings = Substitute.For<IBurnSettingsService>();
        var teamService = Substitute.For<ITeamService>();
        var ctrl = new ShiftsController(
            shiftMgmt, burnSettings, Substitute.For<IShiftSignupService>(),
            Substitute.For<IVolunteerTrackingManagement>(), Substitute.For<IShiftRowView>(), teamService,
            _audit, _userService, Substitute.For<IStringLocalizer<ShiftsResource>>(),
            Substitute.For<IStringLocalizer<OnboardingResource>>(), Substitute.For<IClock>(),
            new ShiftBrowsePageBuilder(shiftMgmt, burnSettings, teamService), NullLogger<ShiftsController>.Instance);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, _actorId.ToString())], "test")),
            },
        };
        return ctrl;
    }
}
