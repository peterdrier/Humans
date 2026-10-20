using AwesomeAssertions;
using Humans.Agent.Domain;
using Humans.Agent.Models;
using System.Security.Claims;
using System.Globalization;
using Humans.Agent.Controllers;
using Humans.Agent.Services;
using Humans.Base.Constants;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Humans.Agent.Tests;

public sealed class AdminAgentControllerTests
{
    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Settings_requires_and_forwards_the_resolved_actor(bool actorExists)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        if (actorExists)
            users.GetUserInfoAsync(actorId, ct).Returns(UserInfo.Create(
                new User { Id = actorId, BurnerName = "Admin" }, [], [], [], null, []));
        var settings = Substitute.For<IAgentSettingsService>();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"))
        };
        var controller = new AdminAgentController(settings, Substitute.For<IAgentService>(),
            Substitute.For<IAgentAdminStatusService>(), Substitute.For<IAgentPreloadCorpusBuilder>(), users)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, Substitute.For<ITempDataProvider>())
        };

        var result = await controller.Settings(new AdminAgentSettingsViewModel(), ct);

        if (actorExists)
        {
            result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#agent");
            await settings.Received(1).UpdateAsync(Arg.Any<Action<AgentSettings>>(), actorId, ct);
        }
        else
        {
            result.Should().BeOfType<UnauthorizedResult>();
            settings.ReceivedCalls().Should().BeEmpty();
        }
    }

    [HumansTheory]
    [Xunit.InlineData("Enabled", false)]
    [Xunit.InlineData("Model", false)]
    [Xunit.InlineData("PreloadConfig", false)]
    [Xunit.InlineData("DailyMessageCap", false)]
    [Xunit.InlineData("HourlyMessageCap", false)]
    [Xunit.InlineData("DailyTokenCap", false)]
    [Xunit.InlineData("RetentionDays", false)]
    [Xunit.InlineData("", false)]
    [Xunit.InlineData("", true)]
    public async Task Settings_require_all_posted_values_before_overwriting_retention_and_limits(string omitted, bool zeroChoices)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var actor = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actor, ct).Returns(UserInfo.Create(new User { Id = actor }, [], [], [], null, []));
        var settings = Substitute.For<IAgentSettingsService>();
        var saved = new AgentSettings();
        settings.UpdateAsync(Arg.Do<Action<AgentSettings>>(update => update(saved)), actor, ct).Returns(Task.CompletedTask);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString())], "test"))
        };
        var controller = new AdminAgentController(settings, Substitute.For<IAgentService>(),
            Substitute.For<IAgentAdminStatusService>(), Substitute.For<IAgentPreloadCorpusBuilder>(), users)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Enabled"] = zeroChoices ? "false" : "true",
            ["Model"] = "selected-model",
            ["PreloadConfig"] = zeroChoices ? "0" : "1",
            ["DailyMessageCap"] = zeroChoices ? "0" : "40",
            ["HourlyMessageCap"] = zeroChoices ? "0" : "20",
            ["DailyTokenCap"] = zeroChoices ? "0" : "60000",
            ["RetentionDays"] = zeroChoices ? "0" : "60"
        };
        if (omitted.Length > 0) fields.Remove(omitted);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(AdminAgentSettingsViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = typeof(AdminAgentSettingsViewModel)
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = (AdminAgentSettingsViewModel)binding.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);

        var result = await controller.Settings(model, ct);

        if (omitted.Length > 0)
        {
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
            controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            settings.ReceivedCalls().Should().BeEmpty();
            saved.RetentionDays.Should().Be(90);
        }
        else
        {
            result.Should().BeOfType<RedirectResult>();
            await settings.Received(1).UpdateAsync(Arg.Any<Action<AgentSettings>>(), actor, ct);
            saved.Enabled.Should().Be(!zeroChoices);
            saved.Model.Should().Be("selected-model");
            saved.PreloadConfig.Should().Be(zeroChoices ? AgentPreloadConfig.Tier1 : AgentPreloadConfig.Tier2);
            saved.DailyMessageCap.Should().Be(zeroChoices ? 0 : 40);
            saved.HourlyMessageCap.Should().Be(zeroChoices ? 0 : 20);
            saved.DailyTokenCap.Should().Be(zeroChoices ? 0 : 60000);
            saved.RetentionDays.Should().Be(zeroChoices ? 0 : 60);
        }
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Reload_reports_the_actual_refresh_outcome(bool succeeded)
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var preload = Substitute.For<IAgentPreloadCorpusBuilder>();
        preload.ReloadAllAsync(cancellationToken).Returns(succeeded);
        var context = new DefaultHttpContext();
        var controller = new AdminAgentController(
            Substitute.For<IAgentSettingsService>(), Substitute.For<IAgentService>(),
            Substitute.For<IAgentAdminStatusService>(), preload, Substitute.For<IUserService>())
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, Substitute.For<ITempDataProvider>())
        };

        var result = await controller.ReloadKnowledgeBase(cancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(AdminAgentController.Status));
        controller.TempData.Should().ContainKey(succeeded ? TempDataKeys.SuccessMessage : TempDataKeys.ErrorMessage);
        controller.TempData.Should().NotContainKey(succeeded ? TempDataKeys.ErrorMessage : TempDataKeys.SuccessMessage);
        await preload.Received(1).ReloadAllAsync(cancellationToken);
    }
}
