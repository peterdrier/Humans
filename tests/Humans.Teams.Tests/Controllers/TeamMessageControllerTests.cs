using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Teams.Controllers;
using Humans.Teams.Models;
using Humans.Teams.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using NodaTime;
using NSubstitute;

namespace Humans.Teams.Tests.Controllers;

/// <summary>
/// Send as team at <c>/Teams/{teamId}/SendMessage/{id}</c>: only a team the viewer may send as
/// (<see cref="ITeamMessageOptionsProvider"/>) is accepted, the recipient's opt-out and the
/// self guard hold, the group is the Reply-To and the audit names the team.
/// </summary>
public sealed class TeamMessageControllerTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ITeamMessageOptionsProvider _teamOptions = Substitute.For<ITeamMessageOptionsProvider>();
    private readonly ICommunicationPreferenceService _commPrefs = Substitute.For<ICommunicationPreferenceService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ILogger<TeamMessageController> _logger = Substitute.For<ILogger<TeamMessageController>>();
    private readonly Guid _viewerId = Guid.NewGuid();
    private readonly Guid _targetId = Guid.NewGuid();
    private readonly Guid _teamId = Guid.NewGuid();
    private readonly TeamMessageController _controller;

    public TeamMessageControllerTests()
    {
        var localizer = Substitute.For<IStringLocalizer<TeamsResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        var shared = Substitute.For<IStringLocalizer<Humans.Base.SharedResource>>();
        shared[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new TeamMessageController(
            _users, _teamOptions, _commPrefs, _emailService, _emailMessages, _audit, localizer, shared, _logger);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _viewerId.ToString())], "TestAuth")),
        };
        _controller.ControllerContext = new ControllerContext { HttpContext = http };
        _controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());

        var viewer = Info(_viewerId, "Coordinator", "coordinator@example.com");
        var target = Info(_targetId, "Target", "target@example.com");
        _users.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>()).Returns(viewer);
        _users.GetUserInfoAsync(_targetId, Arg.Any<CancellationToken>()).Returns(target);
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [_viewerId] = viewer, [_targetId] = target });
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(true);
        _teamOptions.GetOptionsAsync(_viewerId, Arg.Any<CancellationToken>())
            .Returns([new TeamMessageOption(_teamId, "Infrastructure", "infra@nobodies.team")]);
        _emailMessages.FacilitatedMessage(
                Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(ci => new EmailMessage("target@example.com", "Target", ci.ArgAt<Guid?>(0), "Subject", "Body",
                "facilitated_message", ReplyTo: ci.ArgAt<string?>(8)));
    }

    private static UserInfo Info(Guid id, string name, string email, string language = "en") =>
        new(id, name, false, language, null, Instant.FromUnixTimeSeconds(0),
            null, null, null, null, null, null,
            null, null, email, [], [], [], null, []);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [HumansFact]
    public async Task Get_TeamOfferedToViewer_PrefillsTheTeamSender()
    {
        var result = await _controller.SendMessage(_teamId, _targetId, Ct);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<TeamSendMessageViewModel>().Subject;
        model.TeamId.Should().Be(_teamId);
        model.TeamName.Should().Be("Infrastructure");
        model.TeamReplyToEmail.Should().Be("infra@nobodies.team");
        model.RecipientId.Should().Be(_targetId);
        model.SenderEmail.Should().Be("coordinator@example.com");
    }

    [HumansFact]
    public async Task Get_TeamNotOfferedToViewer_IsForbidden()
    {
        var result = await _controller.SendMessage(Guid.NewGuid(), _targetId, Ct);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task Get_OwnProfile_RedirectsToTheProfile()
    {
        var result = await _controller.SendMessage(_teamId, _viewerId, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("ViewProfile");
    }

    [HumansFact]
    public async Task Get_UnknownRecipient_IsNotFound()
    {
        var result = await _controller.SendMessage(_teamId, Guid.NewGuid(), Ct);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Get_RecipientOptedOut_RedirectsWithAnErrorWithoutAskingForTeams()
    {
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _controller.SendMessage(_teamId, _targetId, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("ViewProfile");
        _controller.TempData.Should().ContainKey(Humans.Base.Constants.TempDataKeys.ErrorMessage);
        await _teamOptions.DidNotReceiveWithAnyArgs().GetOptionsAsync(default);
    }

    [HumansFact]
    public async Task Post_TeamNotOfferedToViewer_IsForbiddenAndDoesNotSend()
    {
        var result = await _controller.SendMessage(
            Guid.NewGuid(), _targetId, new TeamSendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<ForbidResult>();
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default(AuditAction), default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task Post_RecipientOptedOut_DoesNotSend()
    {
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _controller.SendMessage(
            _teamId, _targetId, new TeamSendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default(AuditAction), default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task Post_OwnProfile_DoesNotSend()
    {
        var result = await _controller.SendMessage(
            _teamId, _viewerId, new TeamSendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [HumansFact]
    public async Task Post_InvalidModel_RedisplaysTheComposerWithoutSending()
    {
        _controller.ModelState.AddModelError("Message", "Validation_Required");

        var result = await _controller.SendMessage(_teamId, _targetId, new TeamSendMessageViewModel(), Ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TeamSendMessageViewModel>()
            .Which.TeamReplyToEmail.Should().Be("infra@nobodies.team");
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [HumansTheory]
    [Xunit.InlineData("es", "es")]
    [Xunit.InlineData("", "en")]
    [Xunit.InlineData("not-a-culture", "en")]
    [Xunit.InlineData("pt", "en")]
    public async Task Post_TeamOfferedToViewer_UsesGroupReplyToAndAudits(string language, string expectedCulture)
    {
        using var actorCulture = new CultureScope("fr");
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [_viewerId] = Info(_viewerId, "Coordinator", "coordinator@example.com"),
                [_targetId] = Info(_targetId, "Target", "target@example.com", language),
            });

        var result = await _controller.SendMessage(
            _teamId, _targetId, new TeamSendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("ViewProfile");
        _emailMessages.Received(1).FacilitatedMessage(
            _targetId, "target@example.com", "Target", "Coordinator", "Hello", true, "coordinator@example.com",
            expectedCulture, "infra@nobodies.team");
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.ReplyTo == "infra@nobodies.team" && m.UserId == _targetId), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.FacilitatedMessageSent, "User", _targetId,
            Arg.Is<string>(d => d.Contains("from team Infrastructure", StringComparison.Ordinal)
                && !d.Contains("infra@nobodies.team", StringComparison.Ordinal)),
            _viewerId, _teamId, "Team");
    }

    [HumansFact]
    public async Task Post_AuditFailureAfterQueueing_KeepsSuccessAndLogsCritical()
    {
        var failure = new InvalidOperationException("Audit unavailable");
        _audit.LogAsync(Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>()).ThrowsAsync(failure);

        var result = await _controller.SendMessage(
            _teamId, _targetId, new TeamSendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("ViewProfile");
        _controller.TempData.Should().ContainKey(Humans.Base.Constants.TempDataKeys.SuccessMessage);
        await _emailService.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        var log = _logger.ReceivedCalls()
            .Single(call => string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal))
            .GetArguments();
        log[0].Should().Be(LogLevel.Critical);
        log[3].Should().BeSameAs(failure);
        log[2]!.ToString().Should().Contain(_targetId.ToString()).And.Contain(_viewerId.ToString());
    }

    [HumansFact]
    public void Message_CrLfNormalized_OnSet() =>
        new TeamSendMessageViewModel { Message = "a\r\nb" }.Message.Should().Be("a\nb");

    [HumansFact]
    public void Controller_IsAuthorizedAndUsesTheTeamsRoute()
    {
        typeof(TeamMessageController).GetCustomAttribute<AuthorizeAttribute>(inherit: false).Should().NotBeNull();
        typeof(TeamMessageController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("Teams");
        var actions = typeof(TeamMessageController).GetMethods()
            .Where(m => string.Equals(m.Name, nameof(TeamMessageController.SendMessage), StringComparison.Ordinal))
            .ToList();
        actions.Should().HaveCount(2);
        foreach (var method in actions)
        {
            method.GetCustomAttributes<HttpMethodAttribute>().Single().Template
                .Should().Be("{teamId:guid}/SendMessage/{id:guid}");
        }
        actions.Single(m => m.GetParameters().Any(p => p.ParameterType == typeof(TeamSendMessageViewModel)))
            .GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
    }
}
