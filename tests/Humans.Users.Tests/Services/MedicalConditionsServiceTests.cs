using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Users.Controllers;
using Humans.Users.Data.Repositories;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Humans.Camps.Contracts;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Humans.Users.Tests.Services;

public class MedicalConditionsServiceTests
{
    private readonly IUserRepository _repo = Substitute.For<IUserRepository>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly MedicalConditionsService _sut;
    private readonly Guid _actorId = Guid.NewGuid();

    public MedicalConditionsServiceTests() => _sut = new MedicalConditionsService(_repo, _userService, _audit);

    [HumansFact]
    public async Task Reveal_ReturnsTheText_AndAuditsSubjectAndActorWithoutTheText()
    {
        var subjectId = StubMember("Epilepsy");

        var text = await _sut.RevealAsync(subjectId, _actorId);

        text.Should().Be("Epilepsy");
        await _audit.Received(1).LogAsync(UsersAuditActions.MedicalConditionsRevealed, AuditSubjectTypes.User,
            subjectId, Arg.Is<string>(d => !d.Contains("Epilepsy")), _actorId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansTheory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    public async Task Reveal_NothingOnFile_ReturnsNull_AndAuditsNothing(string? onFile)
    {
        var subjectId = StubMember(onFile);

        (await _sut.RevealAsync(subjectId, _actorId)).Should().BeNull();

        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task Reveal_UnknownMember_ReturnsNull_AndAuditsNothing()
    {
        (await _sut.RevealAsync(Guid.NewGuid(), _actorId)).Should().BeNull();

        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task Reveal_MergedAwayId_ReadsAndAuditsTheSurvivor()
    {
        var survivorId = StubMember("Asthma");
        var mergedId = Guid.NewGuid();
        _userService.GetUserInfoAsync(mergedId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = survivorId, PreferredLanguage = "en" }.ToUserInfo());

        (await _sut.RevealAsync(mergedId, _actorId)).Should().Be("Asthma");

        await _audit.Received(1).LogAsync(UsersAuditActions.MedicalConditionsRevealed, AuditSubjectTypes.User,
            survivorId, Arg.Any<string>(), _actorId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public void RevealEndpoint_RequiresTheMedicalPolicyAndAntiforgery()
    {
        var action = typeof(ProfileViewController).GetMethod(nameof(ProfileViewController.RevealMedical))!;

        action.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be(UsersPolicies.MedicalDataViewer);
        action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
        action.GetCustomAttribute<HttpPostAttribute>().Should().NotBeNull();
    }

    [HumansFact]
    public async Task RevealEndpoint_MergedAwaySession_AuditsTheSurvivingActor()
    {
        var subjectId = StubMember("Epilepsy");
        var survivorActor = Guid.NewGuid();
        var controller = Controller(_actorId);
        _userService.GetUserInfoAsync(_actorId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = survivorActor, PreferredLanguage = "en" }.ToUserInfo());

        await controller.RevealMedical(subjectId, CancellationToken.None);

        await _audit.Received(1).LogAsync(UsersAuditActions.MedicalConditionsRevealed, AuditSubjectTypes.User,
            subjectId, Arg.Any<string>(), survivorActor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RevealEndpoint_MissingActor_Forbids_WithoutReadingHealthData()
    {
        var subjectId = StubMember("Epilepsy");

        var result = await Controller(_actorId).RevealMedical(subjectId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        await _repo.DidNotReceiveWithAnyArgs().GetByUserIdReadOnlyAsync(default, default);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task RevealEndpoint_CancelledActorLookup_Propagates_BeforeAnyHealthRead()
    {
        var subjectId = StubMember("Epilepsy");
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        _userService.GetUserInfoAsync(_actorId, aborted.Token).Returns(ValueTask.FromCanceled<UserInfo?>(aborted.Token));

        var reveal = () => Controller(_actorId).RevealMedical(subjectId, aborted.Token);

        await reveal.Should().ThrowAsync<OperationCanceledException>();
        await _repo.DidNotReceiveWithAnyArgs().GetByUserIdReadOnlyAsync(default, default);
    }

    private ProfileViewController Controller(Guid viewerId)
    {
        var users = Substitute.For<IUserManagement>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => _userService.GetUserInfoAsync(ci.ArgAt<Guid>(0), ci.ArgAt<CancellationToken>(1)));
        var controller = new ProfileViewController(users, Substitute.For<IProfilePictureService>(),
            Substitute.For<IShiftService>(),
            Substitute.For<IStringLocalizer<SharedResource>>(), Substitute.For<ITeamService>(),
            Substitute.For<ICampService>(), Substitute.For<IGovernanceService>(),
            Substitute.For<IAuthorizationService>(), _sut);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, viewerId.ToString())], "test")),
            },
        };
        return controller;
    }

    private Guid StubMember(string? medical)
    {
        var id = Guid.NewGuid();
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(new User { Id = id, PreferredLanguage = "en" }.ToUserInfo());
        _repo.GetByUserIdReadOnlyAsync(id, Arg.Any<CancellationToken>())
            .Returns(new Profile { UserId = id, BurnerName = "B", MedicalConditions = medical });
        return id;
    }
}
