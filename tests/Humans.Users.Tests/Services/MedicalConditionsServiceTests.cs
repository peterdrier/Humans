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
