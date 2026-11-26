using Humans.Testing;
using Xunit;
using System.Globalization;
using System.Resources;
using System.Text.Encodings.Web;
using Humans.Base.ViewComponents;
using Humans.Teams.ViewComponents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Google.Contracts;
using Humans.Base.Enums;
using Humans.Teams.Contracts;
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
    public enum RelayAction { Get, Post }
    public enum Participant { Sender, Recipient }
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly ITeamResourceService _resources = Substitute.For<ITeamResourceService>();
    private readonly ICommunicationPreferenceService _commPrefs = Substitute.For<ICommunicationPreferenceService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ILogger<TeamMessageOptionsProvider> _logger = Substitute.For<ILogger<TeamMessageOptionsProvider>>();
    private readonly Guid _viewerId = Guid.NewGuid();
    private readonly Guid _targetId = Guid.NewGuid();
    private readonly Guid _teamId = Guid.NewGuid();
    private readonly TeamMessageController _controller;

    public TeamMessageControllerTests()
    {
        var localizer = Substitute.For<IStringLocalizer<TeamsResource>>().AddFormatting();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        var shared = Substitute.For<IStringLocalizer<Base.SharedResource>>().AddFormatting();
        shared[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        var teamOptions = new TeamMessageOptionsProvider(_teams, _resources, _users, _commPrefs,
            _emailService, _emailMessages, _audit, _logger);
        _controller = new TeamMessageController(_users, teamOptions, localizer, shared,
            Substitute.For<ILogger<TeamMessageController>>());

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
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(true);
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [_teamId] = new(_teamId, "Infrastructure", null, "infra", true, false, SystemTeamType.None,
                false, false, false, false, Instant.MinValue,
                [new TeamMemberInfo(Guid.NewGuid(), _viewerId, "Coordinator", null, null, TeamMemberRole.Coordinator, Instant.MinValue)],
                GoogleGroupPrefix: "infra")
        });
        _resources.GetResourcesByTeamIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>
            {
                [_teamId] = [new(Guid.NewGuid(), _teamId, "123", "Infrastructure", GoogleResourceType.Group,
                    "https://groups.google.com/a/nobodies.team/g/infra", Instant.MinValue, Instant.MinValue, IsActive: true)]
            });
        _emailMessages.FacilitatedMessage(
                Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(ci => new EmailMessage("target@example.com", "Target", ci.ArgAt<Guid?>(0), "Subject", "Body",
                "facilitated_message", ReplyTo: ci.ArgAt<string?>(8)));
    }

    private static UserInfo Info(Guid id, string name, string email, string language = "en") =>
        new(id, name, false, language, null, Instant.FromUnixTimeSeconds(0),
            null, null, null, null, null, null,
            null, null, email, [], [], null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [HumansTheory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_MissingContactChoice_RefusesRelay(bool? includeContact)
    {
        using var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
        _controller.HttpContext.RequestServices = services;
        _controller.Url = Substitute.For<IUrlHelper>();
        var fields = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["Message"] = "Hello"
        };
        if (includeContact is { } choice) fields["IncludeContactInfo"] = choice ? "true" : "false";
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(TeamSendMessageViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext { Metadata = metadata });
        var binding = DefaultModelBindingContext.CreateBindingContext(_controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = (TeamSendMessageViewModel)binding.Result.Model!;
        services.GetRequiredService<Microsoft.AspNetCore.Mvc.ModelBinding.Validation.IObjectModelValidator>()
            .Validate(_controller.ControllerContext, binding.ValidationState, "", model);

        var result = await _controller.SendMessage(_teamId, _targetId, model, Ct);

        if (includeContact is null)
        {
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
            _controller.ModelState[nameof(TeamSendMessageViewModel.IncludeContactInfo)]!.Errors.Should().ContainSingle();
            _emailService.ReceivedCalls().Should().BeEmpty();
            _audit.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            _controller.ModelState.IsValid.Should().BeTrue();
            result.Should().BeOfType<RedirectToActionResult>();
            _emailMessages.Received(1).FacilitatedMessage(Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), includeContact.Value,
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
            await _emailService.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        }
    }

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
    public async Task MessageViews_RenderTheCurrentEncodedRecipient()
    {
        using var culture = new CultureScope("en");
        var result = (ViewResult)await _controller.SendMessage(_teamId, _targetId, Ct);
        const string teamName = "Infrastructure <b>&</b>";
        ((TeamSendMessageViewModel)result.Model!).TeamName = teamName;
        var buttons = new TeamMessageButtonsViewModel(_targetId,
            [new TeamMessageOption(_teamId, teamName, "infra@nobodies.team")]);
        const string currentName = "Renamed <script>alert(1)</script> &";
        _users.GetUserInfoAsync(_targetId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = _targetId, BurnerName = currentName }, [], [], profile: null));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TeamMessageControllerTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(_users);
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(TeamsResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddViewLocalization();
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var template = new ResourceManager(typeof(TeamsResource))
            .GetString("Teams_SendMessage_FromTeam", CultureInfo.CurrentUICulture);
        Assert.NotNull(template);
        var encodedName = HtmlEncoder.Default.Encode(currentName);
        string Expected(string human) =>
            string.Format(CultureInfo.CurrentCulture, template, human, HtmlEncoder.Default.Encode(teamName));
        // The message button is itself a link, so the name inside it renders without one.
        foreach (var (path, model, expected) in new (string, object?, string)[]
        {
            ("/Views/TeamMessage/SendMessage.cshtml", result.Model,
                Expected($"<a href=\"/\" class=\"text-decoration-none\" data-human-popover=\"true\" data-user-id=\"{_targetId}\">{encodedName}</a>")),
            ("/Views/Shared/Components/TeamMessageButtons/Default.cshtml", buttons, Expected($"<span>{encodedName}</span>")),
        })
        {
            var view = services.GetRequiredService<IRazorViewEngine>().GetView(null, path, isMainPage: false);
            Assert.True(view.Success);
            var http = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "viewer")], "test")) };
            var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
            var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
            using var writer = new StringWriter();
            var context = new ViewContext(action, view.View!, data,
                new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
            await view.View!.RenderAsync(context);
            var html = writer.ToString();
            Assert.Contains(expected, html, StringComparison.Ordinal);
            Assert.DoesNotContain(currentName, html, StringComparison.Ordinal);
        }
    }

    [HumansFact]
    public async Task Get_TeamNotOfferedToViewer_IsForbidden()
    {
        var result = await _controller.SendMessage(Guid.NewGuid(), _targetId, Ct);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansTheory]
    [InlineData(RelayAction.Get, Participant.Sender)]
    [InlineData(RelayAction.Get, Participant.Recipient)]
    [InlineData(RelayAction.Post, Participant.Sender)]
    [InlineData(RelayAction.Post, Participant.Recipient)]
    public async Task Compose_TombstoneParticipant_RefusesRelay(RelayAction action, Participant participant)
    {
        var sender = Info(_viewerId, "Sender", "sender@example.org");
        var recipient = Info(_targetId, "Recipient", "recipient@example.org");
        if (participant == Participant.Sender) sender = sender with { State = UserState.Deleted };
        else recipient = recipient with { State = UserState.Deleted };
        _users.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>()).Returns(sender);
        _users.GetUserInfoAsync(_targetId, Arg.Any<CancellationToken>()).Returns(recipient);

        var result = action == RelayAction.Get
            ? await _controller.SendMessage(_teamId, _targetId, Ct)
            : await _controller.SendMessage(_teamId, _targetId, new TeamSendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<NotFoundResult>();
        _emailService.ReceivedCalls().Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
        _teams.ReceivedCalls().Should().BeEmpty();
        _commPrefs.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Send_TombstoneContext_RefusesQueue()
    {
        var provider = new TeamMessageOptionsProvider(_teams, _resources, _users, _commPrefs,
            _emailService, _emailMessages, _audit, _logger);
        var team = new TeamMessageOption(_teamId, "Team", "team@example.org");
        foreach (var participant in Enum.GetValues<Participant>())
        {
            var sender = Info(_viewerId, "Sender", "sender@example.org");
            var recipient = Info(_targetId, "Recipient", "recipient@example.org");
            if (participant == Participant.Sender) sender = sender with { State = UserState.Merged };
            else recipient = recipient with { State = UserState.Deleted };

            var result = await provider.SendAsync(new TeamMessageContext(sender, recipient, [team]), team, "Hello", false);

            result.Should().BeFalse();
        }
        _emailService.ReceivedCalls().Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
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
        _controller.TempData.Should().ContainKey(Base.Constants.TempDataKeys.ErrorMessage);
        await _teams.DidNotReceiveWithAnyArgs().GetTeamsAsync();
    }

    [HumansFact]
    public async Task Post_TeamNotOfferedToViewer_IsForbiddenAndDoesNotSend()
    {
        var result = await _controller.SendMessage(
            Guid.NewGuid(), _targetId, new TeamSendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<ForbidResult>();
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(null!, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task Post_RecipientOptedOut_DoesNotSend()
    {
        _commPrefs.AcceptsFacilitatedMessagesAsync(_targetId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _controller.SendMessage(
            _teamId, _targetId, new TeamSendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(null!, default!, default, default!, default(Guid));
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
    [InlineData("es", "es")]
    [InlineData("", "en")]
    [InlineData("not-a-culture", "en")]
    [InlineData("pt", "en")]
    public async Task Post_TeamOfferedToViewer_UsesGroupReplyToAndAudits(string language, string expectedCulture)
    {
        using var actorCulture = new CultureScope("fr");
        _users.GetUserInfoAsync(_targetId, Arg.Any<CancellationToken>())
            .Returns(Info(_targetId, "Target", "target@example.com", language));

        var result = await _controller.SendMessage(
            _teamId, _targetId, new TeamSendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("ViewProfile");
        _emailMessages.Received(1).FacilitatedMessage(
            _targetId, "target@example.com", "Target", "Coordinator", "Hello", true, "coordinator@example.com",
            expectedCulture, "infra@nobodies.team");
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.ReplyTo == "infra@nobodies.team" && m.UserId == _targetId), CancellationToken.None);
        await _audit.Received(1).LogAsync(
            EmailAuditActions.FacilitatedMessageSent, "User", _targetId,
            Arg.Is<string>(d => d.Contains("from team Infrastructure", StringComparison.Ordinal)
                && !d.Contains("infra@nobodies.team", StringComparison.Ordinal)),
            _viewerId, _teamId, "Team");
    }

    [HumansFact]
    public async Task Post_AuditFailureAfterQueueing_KeepsSuccessAndLogsCritical()
    {
        var failure = new InvalidOperationException("Audit unavailable");
        _audit.LogAsync(Arg.Any<AuditActionDefinition>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>()).ThrowsAsync(failure);

        var result = await _controller.SendMessage(
            _teamId, _targetId, new TeamSendMessageViewModel { Message = "Hello" }, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("ViewProfile");
        _controller.TempData.Should().ContainKey(Base.Constants.TempDataKeys.SuccessMessage);
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
