using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.Primitives;
using Humans.AuditLog.Contracts;
using Humans.Base.Csv;
using Humans.Base.Enums;
using AwesomeAssertions;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using Xunit;
using NodaTime;
using NSubstitute;

namespace Humans.Camps.Tests.Controllers;

public class CampAdminControllerTests
{
    private static readonly Guid ActiveEventId = Guid.NewGuid();

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_AuditsAdministratorAndEmittedRows_IncludingEmptyExports(bool hasSeason)
    {
        var camps = Substitute.For<ICampManagement>();
        var users = Substitute.For<IUserService>();
        var audit = Substitute.For<IAuditLogService>();
        var actorId = Guid.NewGuid();
        var leadId = Guid.NewGuid();
        var camp = new CampInfo(Guid.NewGuid(), "camp", "contact@example.org", "+34600000000", false, 1, []);
        if (hasSeason)
            camp = camp with
            {
                Seasons = [new CampSeasonInfo(Guid.NewGuid(), camp.Id, camp.Slug, 2026, null,
                    "Camp name", "", "en", [], CampSeasonStatus.Active, YesNoMaybe.Yes, YesNoMaybe.Yes,
                    AdultPlayspacePolicy.No, 12, null, null, null, 0, null, null) { LeadUserIds = [leadId] }]
            };
        var noSeason = camp with { Id = Guid.NewGuid(), Slug = "no-season", Seasons = [] };
        var controller = CreateController(camps, Substitute.For<ICampRoleService>(), users, actorId, audit: audit);
        var ct = controller.HttpContext.RequestAborted;
        var eventId = Guid.NewGuid();
        camps.GetSettingsAsync(ct).Returns(new CampSettingsInfo(2026, []) { ActiveEventSettingsId = eventId });
        camps.GetCampsForEventAsync(eventId, ct).Returns(new List<CampInfo> { camp, noSeason });
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), ct)
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [leadId] = MakeUser(leadId) with { BurnerName = "Lead", IdentityEmailColumn = "lead@example.org" }
            });

        var file = (await controller.ExportCamps()).Should().BeOfType<FileContentResult>().Which;

        var csv = HumansCsv.Utf8WithBom.GetString(file.FileContents);
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(hasSeason ? 2 : 1);
        if (hasSeason)
            csv.Should().Contain("contact@example.org").And.Contain("Lead <lead@example.org>");
        file.FileDownloadName.Should().Be("barrios-2026.csv");
        await audit.Received(1).LogAsync(CampsAuditActions.CampsExported, "Camps", Guid.Empty,
            $"Camp contact and lead list exported for 2026: {(hasSeason ? 1 : 0)} rows", actorId);
    }

    [HumansFact]
    public async Task EeSlotCount_DependencyFailurePropagatesWithoutValidationFeedback()
    {
        var camps = Substitute.For<ICampManagement>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(MakeUser(actorId));
        var failure = new InvalidOperationException("Storage unavailable");
        camps.SetCampSeasonEeSlotCountAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CampAdminMutationResult>(failure));
        var controller = CreateController(camps, Substitute.For<ICampRoleService>(), users, actorId);

        Func<Task> act = async () => await controller.SetCampSeasonEeSlotCount(Guid.NewGuid(), 3, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        controller.TempData.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("EE slot count cannot be negative.")]
    [InlineData("Camp season not found.")]
    public async Task EeSlotCount_ExpectedRefusalShowsReasonWithoutSuccess(string reason)
    {
        var camps = Substitute.For<ICampManagement>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(MakeUser(actorId));
        camps.SetCampSeasonEeSlotCountAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new CampAdminMutationResult(false, reason));
        var controller = CreateController(camps, Substitute.For<ICampRoleService>(), users, actorId);

        (await controller.SetCampSeasonEeSlotCount(Guid.NewGuid(), 3, TestContext.Current.CancellationToken))
            .Should().BeOfType<RedirectToActionResult>();

        controller.TempData[Humans.Base.Constants.TempDataKeys.ErrorMessage].Should().Be(reason);
        controller.TempData.Should().ContainSingle();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EeSlotCount_RejectsMalformedBinding_ButAcceptsExplicitZero(bool malformed)
    {
        var camps = Substitute.For<ICampManagement>();
        camps.SetCampSeasonEeSlotCountAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new CampAdminMutationResult(true));
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(MakeUser(actorId));
        var controller = CreateController(camps, Substitute.For<ICampRoleService>(), users, actorId);
        if (malformed)
            controller.ModelState.AddModelError("slotCount", "Not a number.");

        var result = await controller.SetCampSeasonEeSlotCount(seasonId, 0, ct);

        if (malformed)
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            await camps.DidNotReceive().SetCampSeasonEeSlotCountAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            await camps.Received(1).SetCampSeasonEeSlotCountAsync(seasonId, 0, actorId, ct);
        }
    }

    [HumansTheory]
    [InlineData(nameof(CampAdminController.SetNameLockDate), "eventSettingsId", null)]
    [InlineData(nameof(CampAdminController.SetNameLockDate), "eventSettingsId", "invalid")]
    [InlineData(nameof(CampAdminController.SetCampSeasonEeSlotCount), "slotCount", null)]
    public async Task RequiredAdminForms_RejectMissingOrMalformedValuesBeforeMutation(
        string action, string field, string? submitted)
    {
        var camps = Substitute.For<ICampManagement>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(MakeUser(actorId));
        var controller = CreateController(camps, Substitute.For<ICampRoleService>(), users, actorId);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var parameter = typeof(CampAdminController).GetMethod(action)!.GetParameters()
            .Single(p => string.Equals(p.Name, field, StringComparison.Ordinal));
        var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>())
            .GetMetadataForParameter(parameter);
        var descriptor = new ControllerParameterDescriptor
        {
            Name = parameter.Name!,
            ParameterType = parameter.ParameterType,
            ParameterInfo = parameter
        };
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = parameter
        });
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        if (submitted is not null)
            fields[field] = submitted;
        await services.GetRequiredService<ParameterBinder>().BindModelAsync(controller.ControllerContext, binder,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            descriptor, metadata, value: null);

        var result = action switch
        {
            nameof(CampAdminController.SetNameLockDate) => await controller.SetNameLockDate(Guid.Empty, "2027-05-01"),
            nameof(CampAdminController.SetCampSeasonEeSlotCount) =>
                await controller.SetCampSeasonEeSlotCount(Guid.NewGuid(), 0, TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        result.Should().BeOfType<BadRequestObjectResult>();
        camps.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("Open", true)]
    [InlineData("Close", true)]
    [InlineData("LockDate", true)]
    [InlineData("Open", false)]
    [InlineData("Close", false)]
    [InlineData("LockDate", false)]
    [InlineData("Reactivate", true)]
    [InlineData("Delete", true)]
    [InlineData("Reactivate", false)]
    [InlineData("Delete", false)]
    public async Task AuditedAdminAction_PassesTheSignedInActorOrRefusesMissingUser(string action, bool userExists)
    {
        var camps = Substitute.For<ICampManagement>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        if (userExists)
            users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
                .Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        camps.ReactivateSeasonAsync(targetId, actorId).Returns(new CampAdminMutationResult(true));
        camps.DeleteCampAsync(targetId, actorId).Returns(new CampAdminMutationResult(true));
        var controller = CreateController(camps, Substitute.For<ICampRoleService>(), users, actorId);
        var lockDate = new LocalDate(2026, 3, 1);
        var eventId = Guid.NewGuid();

        var result = action switch
        {
            "Open" => await controller.OpenSeason(eventId),
            "Close" => await controller.CloseSeason(eventId),
            "LockDate" => await controller.SetNameLockDate(eventId, "2026-03-01"),
            "Reactivate" => await controller.Reactivate(targetId, null),
            "Delete" => await controller.Delete(targetId),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        if (!userExists)
        {
            result.Should().BeOfType<UnauthorizedResult>();
            camps.ReceivedCalls().Should().BeEmpty();
            return;
        }

        switch (action)
        {
            case "Open": await camps.Received(1).OpenSeasonAsync(eventId, actorId); break;
            case "Close": await camps.Received(1).CloseSeasonAsync(eventId, actorId); break;
            case "LockDate": await camps.Received(1).SetNameLockDateAsync(eventId, lockDate, actorId); break;
            case "Reactivate": await camps.Received(1).ReactivateSeasonAsync(targetId, actorId); break;
            case "Delete": await camps.Received(1).DeleteCampAsync(targetId, actorId); break;
        }
    }

    [HumansTheory]
    [InlineData(false, "settings")]
    [InlineData(false, "camps")]
    [InlineData(false, "users")]
    [InlineData(true, "settings")]
    [InlineData(true, "camps")]
    [InlineData(true, "roles")]
    public async Task AdminRead_PropagatesRequestCancellationAtEveryRead(bool dashboard, string boundary)
    {
        var camps = Substitute.For<ICampManagement>();
        var roles = Substitute.For<ICampRoleService>();
        var users = Substitute.For<IUserService>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (string.Equals(boundary, "settings", StringComparison.Ordinal))
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new CampSettingsInfo(2026, []) { ActiveEventSettingsId = ActiveEventId };
        });
        camps.GetCampsForEventAsync(ActiveEventId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (string.Equals(boundary, "camps", StringComparison.Ordinal))
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return [];
        });
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (string.Equals(boundary, "users", StringComparison.Ordinal))
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new Dictionary<Guid, UserInfo>();
        });
        roles.GetMissingSpecialRolesAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (string.Equals(boundary, "roles", StringComparison.Ordinal))
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return [];
        });
        var logger = Substitute.For<ILogger<CampAdminController>>();
        var controller = CreateController(camps, roles, users, Guid.NewGuid(), logger);
        controller.HttpContext.RequestAborted = cancellation.Token;
        Func<Task<IActionResult>> read = dashboard ? controller.Index : controller.ExportCamps;

        await read.Should().ThrowAsync<OperationCanceledException>();
        controller.TempData.Should().BeEmpty();
        logger.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task DeactivateRole_PropagatesRequestCancellation()
    {
        var camps = Substitute.For<ICampManagement>();
        var roles = Substitute.For<ICampRoleService>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        users.GetUserInfoAsync(actorId, cancellation.Token)
            .Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        roles.DeactivateDefinitionAsync(Arg.Any<Guid>(), actorId, cancellation.Token)
            .Returns(Task.FromCanceled<bool>(cancellation.Token));

        var controller = CreateController(camps, roles, users, actorId);

        var act = () => controller.DeactivateRole(Guid.NewGuid(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData("Approve")]
    [InlineData("Reject")]
    [InlineData("Reactivate")]
    [InlineData("Delete")]
    public async Task LifecycleMutation_InvalidOperationFromDependency_Propagates(string action)
    {
        var camps = Substitute.For<ICampManagement>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var failure = new InvalidOperationException("Database transaction unavailable");
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(MakeUser(actorId));
        ConfigureFailure(camps, action, seasonId, actorId, failure);
        var controller = CreateController(camps, Substitute.For<ICampRoleService>(), users, actorId);

        var act = () => InvokeActionAsync(controller, action, seasonId);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        controller.TempData.Should().NotContainKey("ErrorMessage");
    }

    [HumansTheory]
    [InlineData("Approve")]
    [InlineData("Reject")]
    [InlineData("Reactivate")]
    [InlineData("Delete")]
    public async Task LifecycleAction_ExpectedRejection_LogsWarningWithoutException(string action)
    {
        var camps = Substitute.For<ICampManagement>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var logger = Substitute.For<ILogger<CampAdminController>>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        var reason = string.Equals(action, "Delete", StringComparison.Ordinal) ? "Camp not found." : "Season not found.";
        switch (action)
        {
            case "Approve": camps.ApproveSeasonAsync(targetId, actorId, null).Returns(new CampAdminMutationResult(false, reason)); break;
            case "Reject": camps.RejectSeasonAsync(targetId, actorId, "Rejected").Returns(new CampAdminMutationResult(false, reason)); break;
            case "Reactivate": camps.ReactivateSeasonAsync(targetId, actorId).Returns(new CampAdminMutationResult(false, reason)); break;
            case "Delete": camps.DeleteCampAsync(targetId, actorId).Returns(new CampAdminMutationResult(false, reason)); break;
        }
        var controller = CreateController(camps, Substitute.For<ICampRoleService>(), users, actorId, logger);

        var result = await InvokeActionAsync(controller, action, targetId);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Index");
        controller.TempData["ErrorMessage"].Should().Be(reason);
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Warning);
        args[3].Should().BeNull();
        args[2]!.ToString().Should().Contain(targetId.ToString()).And.Contain(reason);
        if (action is "Approve" or "Reject") args[2]!.ToString().Should().Contain(actorId.ToString());
    }

    [HumansTheory]
    [InlineData("Approve")]
    [InlineData("Reject")]
    [InlineData("Reactivate")]
    [InlineData("Delete")]
    public async Task LifecycleAction_UnexpectedFailure_Propagates(string action)
    {
        var camps = Substitute.For<ICampManagement>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        ConfigureFailure(camps, action, targetId, actorId, new IOException("Storage unavailable"));
        var controller = CreateController(camps, Substitute.For<ICampRoleService>(), users, actorId);

        var act = () => InvokeActionAsync(controller, action, targetId);

        await act.Should().ThrowAsync<IOException>().WithMessage("Storage unavailable");
    }

    [HumansTheory]
    [InlineData(false, nameof(CampRoleDefinitionFormViewModel.SlotCount))]
    [InlineData(false, nameof(CampRoleDefinitionFormViewModel.MinimumRequired))]
    [InlineData(false, nameof(CampRoleDefinitionFormViewModel.SortOrder))]
    [InlineData(true, nameof(CampRoleDefinitionFormViewModel.SlotCount))]
    [InlineData(true, nameof(CampRoleDefinitionFormViewModel.MinimumRequired))]
    [InlineData(true, nameof(CampRoleDefinitionFormViewModel.SortOrder))]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public async Task RoleForms_RequirePostedCountsAndOrderBeforeMutation(bool edit, string? omitted)
    {
        var roles = Substitute.For<ICampRoleService>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(MakeUser(actorId));
        roles.CreateDefinitionAsync(Arg.Any<CampRoleDefinitionInput>(), actorId, Arg.Any<CancellationToken>())
            .Returns(new CampAdminMutationResult(true));
        roles.UpdateDefinitionAsync(roleId, Arg.Any<CampRoleDefinitionInput>(), actorId, Arg.Any<CancellationToken>())
            .Returns(new UpdateCampRoleDefinitionResult(UpdateCampRoleDefinitionStatus.Updated, "Updated"));
        var controller = CreateController(Substitute.For<ICampManagement>(), roles, users, actorId);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Name"] = "Worker",
            ["Slug"] = "worker",
            ["SlotCount"] = "3",
            ["MinimumRequired"] = "0",
            ["SortOrder"] = "0"
        };
        if (omitted is not null)
            fields.Remove(omitted);
        var metadata = services.GetRequiredService<IModelMetadataProvider>()
            .GetMetadataForType(typeof(CampRoleDefinitionFormViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = typeof(CampRoleDefinitionFormViewModel)
        });
        var context = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(context);
        var form = (CampRoleDefinitionFormViewModel)context.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", form);
        var ct = TestContext.Current.CancellationToken;

        var result = edit ? await controller.EditRole(roleId, form, ct) : await controller.CreateRole(form, ct);

        if (omitted is not null)
        {
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(form);
            controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            roles.ReceivedCalls().Should().BeEmpty();
            controller.TempData.Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            if (edit)
                await roles.Received(1).UpdateDefinitionAsync(roleId, Arg.Is<CampRoleDefinitionInput>(
                    input => input.SlotCount == 3 && input.MinimumRequired == 0 && input.SortOrder == 0), actorId, ct);
            else
                await roles.Received(1).CreateDefinitionAsync(Arg.Is<CampRoleDefinitionInput>(
                    input => input.SlotCount == 3 && input.MinimumRequired == 0 && input.SortOrder == 0), actorId, ct);
        }
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoleForm_ExpectedRuleRejection_LogsWarningAndRetainsForm(bool edit)
    {
        var roles = Substitute.For<ICampRoleService>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        var reason = edit ? "Camp role 'Worker' is a system role; only SlotCount and Description can be changed."
            : "A camp role definition with slug 'worker' already exists.";
        roles.CreateDefinitionAsync(Arg.Any<CampRoleDefinitionInput>(), actorId, Arg.Any<CancellationToken>())
            .Returns(new CampAdminMutationResult(false, reason));
        roles.UpdateDefinitionAsync(roleId, Arg.Any<CampRoleDefinitionInput>(), actorId, Arg.Any<CancellationToken>())
            .Returns(UpdateCampRoleDefinitionResult.Rejected(reason));
        var logger = Substitute.For<ILogger<CampAdminController>>();
        var controller = CreateController(Substitute.For<ICampManagement>(), roles, users, actorId, logger);
        var form = new CampRoleDefinitionFormViewModel { Name = "Worker", Slug = "worker" };
        var ct = TestContext.Current.CancellationToken;

        var result = edit ? await controller.EditRole(roleId, form, ct) : await controller.CreateRole(form, ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(form);
        controller.ModelState[string.Empty]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(reason);
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Warning);
        args[3].Should().BeNull();
        args[2]!.ToString().Should().Contain(actorId.ToString()).And.Contain(reason);
        if (edit) args[2]!.ToString().Should().Contain(roleId.ToString());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoleForm_UnexpectedInvalidOperation_LogsErrorAndHidesExceptionMessage(bool edit)
    {
        var roles = Substitute.For<ICampRoleService>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        var failure = new InvalidOperationException("Database connection is unavailable.");
        roles.CreateDefinitionAsync(Arg.Any<CampRoleDefinitionInput>(), actorId, Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        roles.UpdateDefinitionAsync(roleId, Arg.Any<CampRoleDefinitionInput>(), actorId, Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        var logger = Substitute.For<ILogger<CampAdminController>>();
        var controller = CreateController(Substitute.For<ICampManagement>(), roles, users, actorId, logger);
        var form = new CampRoleDefinitionFormViewModel { Name = "Worker", Slug = "worker" };
        var ct = TestContext.Current.CancellationToken;

        var result = edit ? await controller.EditRole(roleId, form, ct) : await controller.CreateRole(form, ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(form);
        controller.ModelState[string.Empty]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should()
            .Be(edit ? "Failed to update role definition." : "Failed to create role definition.");
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Error);
        args[3].Should().BeSameAs(failure);
    }

    [HumansFact]
    public async Task EditRole_UnexpectedResult_LogsErrorAndRetainsForm()
    {
        var roles = Substitute.For<ICampRoleService>();
        var users = Substitute.For<IUserService>();
        var actorId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        roles.UpdateDefinitionAsync(roleId, Arg.Any<CampRoleDefinitionInput>(), actorId, Arg.Any<CancellationToken>())
            .Returns(new UpdateCampRoleDefinitionResult((UpdateCampRoleDefinitionStatus)99, string.Empty));
        var logger = Substitute.For<ILogger<CampAdminController>>();
        var controller = CreateController(Substitute.For<ICampManagement>(), roles, users, actorId, logger);
        var form = new CampRoleDefinitionFormViewModel { Name = "Worker", Slug = "worker" };

        var result = await controller.EditRole(roleId, form, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(form);
        controller.ModelState[string.Empty]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be("Failed to update role definition.");
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Error);
        args[3].Should().BeOfType<System.Diagnostics.UnreachableException>().Which.Message.Should().Contain("99");
        args[2]!.ToString().Should().Contain(roleId.ToString());
    }

    private static void ConfigureFailure(ICampManagement camps, string action, Guid targetId, Guid actorId, Exception failure)
    {
        switch (action)
        {
            case "Approve": camps.ApproveSeasonAsync(targetId, actorId, null).ThrowsAsync(failure); break;
            case "Reject": camps.RejectSeasonAsync(targetId, actorId, "Rejected").ThrowsAsync(failure); break;
            case "Reactivate": camps.ReactivateSeasonAsync(targetId, actorUserId: actorId).ThrowsAsync(failure); break;
            case "Delete": camps.DeleteCampAsync(targetId, actorUserId: actorId).ThrowsAsync(failure); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private static Task<IActionResult> InvokeActionAsync(CampAdminController controller, string action, Guid targetId) => action switch
    {
        "Approve" => controller.Approve(targetId, null),
        "Reject" => controller.Reject(targetId, "Rejected"),
        "Reactivate" => controller.Reactivate(targetId, null),
        "Delete" => controller.Delete(targetId),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static CampAdminController CreateController(
        ICampManagement camps, ICampRoleService roles, IUserService users, Guid actorId, ILogger<CampAdminController>? logger = null, IAuditLogService? audit = null)
    {
        var controller = new CampAdminController(
            camps,
            roles,
            new CampAdminPageBuilder(camps, roles),
            new CampCsvExportBuilder(camps, users, audit ?? Substitute.For<IAuditLogService>()),
            users,
            logger ?? NullLogger<CampAdminController>.Instance);
        var services = new ServiceCollection().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], authenticationType: "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = nameof(CampAdminController.DeactivateRole) },
        };
        controller.Url = Substitute.For<IUrlHelper>();
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    private static UserInfo MakeUser(Guid id) => new(
        id, "actor", false, "en", null, SystemClock.Instance.GetCurrentInstant(), null,
        null, null, null, null, null, null, null, null, [], [], [], null, []);
}
