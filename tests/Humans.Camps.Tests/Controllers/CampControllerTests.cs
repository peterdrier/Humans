using System.Security.Claims;
using System.Reflection;
using System.Globalization;
using AwesomeAssertions;
using Humans.CityPlanning.Contracts;
using Humans.Shifts.Contracts;
using Humans.Base.Enums;
using Humans.Base.Constants;
using Humans.Base;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NSubstitute.ExceptionExtensions;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;
using Xunit;

namespace Humans.Camps.Tests.Controllers;

public class CampControllerTests
{
    private static readonly CampEventRegistration Event2026 = new(Guid.NewGuid(), "Nowhere 2026", 2026, true);
    private static readonly CampEventRegistration Event2027 = new(Guid.NewGuid(), "Nowhere 2027", 2027, false);
    private static readonly CampEventRegistration Event2029 = new(Guid.NewGuid(), "Nowhere 2029", 2029, false);

    private readonly ICampManagement _camps = Substitute.For<ICampManagement>();
    private readonly ICampContactService _contacts = Substitute.For<ICampContactService>();
    private readonly ICampRoleService _roles = Substitute.For<ICampRoleService>();
    private readonly ICityPlanningService _cityPlanning = Substitute.For<ICityPlanningService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IStringLocalizer<CampsResource> _campsLocalizer = Substitute.For<IStringLocalizer<CampsResource>>();
    private readonly IStringLocalizer<SharedResource> _sharedLocalizer = Substitute.For<IStringLocalizer<SharedResource>>();

    public enum ContactSurface { Form, Redisplay, Send }
    public enum ContactViewer { Other, Lead, CampsAdmin, Admin }

    [HumansTheory]
    [InlineData(ContactSurface.Form)]
    [InlineData(ContactSurface.Redisplay)]
    [InlineData(ContactSurface.Send)]
    public async Task Contact_UsesTheActiveEventsSeasonForNamesAndLeadNotices(ContactSurface surface)
    {
        using var request = new CancellationTokenSource();
        var senderId = Guid.NewGuid();
        var activeLead = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Active camp", CampSeasonStatus.Active, activeLead);
        var active = camp.Seasons[0];
        var other = active with { Id = Guid.NewGuid(), EventSettingsId = Guid.NewGuid(), Name = "Other event", LeadUserIds = [Guid.NewGuid()] };
        var future = MakeCamp("alpha", "Future camp", CampSeasonStatus.Active, Guid.NewGuid(), year: 2027).Seasons[0]
            with { CampId = camp.Id };
        camp = camp with { Seasons = [future, other, active] };
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(Settings2026());
        _users.GetUserInfoAsync(senderId, Arg.Any<CancellationToken>()).Returns(UserInfo.Create(
            new User { Id = senderId, Email = "sender@example.com", BurnerName = "Sender" }, [], [], null));
        _campsLocalizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        _contacts.SendFacilitatedMessageAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<string>()).Returns(new CampContactResult(true, false));
        var controller = BuildController(senderId);
        controller.HttpContext.RequestAborted = request.Token;
        var model = new CampContactViewModel { Message = "Hello", CampName = "Posted name", IncludeContactInfo = false };
        if (surface == ContactSurface.Redisplay) controller.ModelState.AddModelError("Message", "Required");

        var result = surface == ContactSurface.Form
            ? await controller.Contact(camp.Slug)
            : await controller.Contact(camp.Slug, model);

        if (surface == ContactSurface.Send)
        {
            result.Should().BeOfType<RedirectToActionResult>();
            await _contacts.Received(1).SendFacilitatedMessageAsync(camp.Id, camp.ContactEmail, "Active camp",
                senderId, "Sender", "sender@example.com", "Hello", false,
                Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { activeLead })), $"/Barrios/{camp.Slug}");
        }
        else
        {
            result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<CampContactViewModel>()
                .Subject.CampName.Should().Be("Active camp");
            _contacts.ReceivedCalls().Should().BeEmpty();
        }
        await _camps.Received(1).GetSettingsAsync(request.Token);
    }

    [HumansTheory]
    [InlineData(ContactSurface.Form, ContactViewer.Other)]
    [InlineData(ContactSurface.Send, ContactViewer.Other)]
    [InlineData(ContactSurface.Form, ContactViewer.Lead)]
    [InlineData(ContactSurface.Send, ContactViewer.Lead)]
    [InlineData(ContactSurface.Form, ContactViewer.CampsAdmin)]
    [InlineData(ContactSurface.Send, ContactViewer.CampsAdmin)]
    [InlineData(ContactSurface.Form, ContactViewer.Admin)]
    [InlineData(ContactSurface.Send, ContactViewer.Admin)]
    public async Task Contact_NonPublicSeason_UsesTheDetailPagesViewerGate(ContactSurface surface, ContactViewer viewer)
    {
        var userId = Guid.NewGuid();
        var camp = MakeCamp("private", "Private camp", CampSeasonStatus.Pending,
            viewer == ContactViewer.Lead ? userId : Guid.NewGuid());
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(Settings2026());
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(userId));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(viewer == ContactViewer.Other ? AuthorizationResult.Failed() : AuthorizationResult.Success());
        _contacts.SendFacilitatedMessageAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<string>()).Returns(new CampContactResult(true, false));
        _campsLocalizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = BuildController(userId);
        if (viewer is ContactViewer.Admin or ContactViewer.CampsAdmin)
            controller.User.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.Role,
                viewer == ContactViewer.Admin ? RoleNames.Admin : CampsRoles.CampsAdmin)]));

        var result = surface == ContactSurface.Form
            ? await controller.Contact(camp.Slug)
            : await controller.Contact(camp.Slug, new CampContactViewModel { Message = "Hello" });

        if (viewer == ContactViewer.Other)
        {
            result.Should().BeOfType<NotFoundResult>();
            _contacts.ReceivedCalls().Should().BeEmpty();
        }
        else if (surface == ContactSurface.Form)
            result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<CampContactViewModel>()
                .Subject.CampName.Should().Be("Private camp");
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            _contacts.ReceivedCalls().Should().ContainSingle();
        }
    }

    [HumansFact]
    public async Task Contact_MissingActor_CannotLoadTheForm()
    {
        var camp = MakeCamp("alpha", "Alpha", CampSeasonStatus.Active);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);

        var result = await BuildController(Guid.NewGuid()).Contact(camp.Slug);

        result.Should().BeOfType<UnauthorizedResult>();
        _contacts.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Contact_CancelledDisplaySettingsRead_DoesNotSend()
    {
        using var request = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha", CampSeasonStatus.Active);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(userId));
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await request.CancelAsync();
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Settings2026();
        });
        var controller = BuildController(userId);
        controller.HttpContext.RequestAborted = request.Token;

        var send = () => controller.Contact(camp.Slug, new CampContactViewModel { Message = "Hello" });

        await send.Should().ThrowAsync<OperationCanceledException>();
        _contacts.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false, "AcceptingMembers")]
    [InlineData(false, "KidsWelcome")]
    [InlineData(false, "KidsVisiting")]
    [InlineData(false, "HasPerformanceSpace")]
    [InlineData(false, "AdultPlayspace")]
    [InlineData(false, "SpaceRequirement")]
    [InlineData(false, "SoundZone")]
    [InlineData(false, "ElectricalGrid")]
    [InlineData(false, "")]
    [InlineData(true, "AcceptingMembers")]
    [InlineData(true, "KidsWelcome")]
    [InlineData(true, "KidsVisiting")]
    [InlineData(true, "HasPerformanceSpace")]
    [InlineData(true, "AdultPlayspace")]
    [InlineData(true, "SpaceRequirement")]
    [InlineData(true, "SoundZone")]
    [InlineData(true, "ElectricalGrid")]
    [InlineData(true, "")]
    [InlineData(false, "MemberCount")]
    [InlineData(true, "MemberCount")]
    [InlineData(false, "", "0")]
    [InlineData(true, "", "0")]
    [InlineData(false, "", "-1")]
    [InlineData(true, "", "-1")]
    [InlineData(false, "", "1", -1)]
    [InlineData(true, "", "1", -1)]
    [InlineData(false, "", "1", 1)]
    [InlineData(true, "", "1", 1)]
    public async Task Camp_forms_require_posted_choices_and_valid_counts(
        bool edit, string omitted, string memberCount = "1", int timesAtNowhere = 0)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var userId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: userId);
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(Settings2026());
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(userId));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _camps.UpdateCampAsync(Arg.Any<CampUpdateInput>(), userId).Returns(CampUpdateResult.Success());
        _camps.CreateCampAsync(default, default!, default!, default!, null, null, false, 0, default!, null, Guid.Empty)
            .ReturnsForAnyArgs(new CampWriteResult<Camp>(new Camp { Id = camp.Id, Slug = camp.Slug }));
        _campsLocalizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = BuildController(userId);
        controller.HttpContext.RequestServices = services;
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Name"] = "Alpha Camp",
            ["ContactEmail"] = "alpha@example.com",
            ["ContactPhone"] = "+34600000000",
            ["BlurbLong"] = "Long description",
            ["BlurbShort"] = "Short description",
            ["Languages"] = "en",
            ["SeasonId"] = camp.Seasons[0].Id.ToString(),
            ["MemberCount"] = memberCount,
            ["TimesAtNowhere"] = timesAtNowhere.ToString(CultureInfo.InvariantCulture),
            ["AcceptingMembers"] = "0",
            ["KidsWelcome"] = "0",
            ["KidsVisiting"] = "0",
            ["HasPerformanceSpace"] = "0",
            ["AdultPlayspace"] = "0",
            ["SpaceRequirement"] = "",
            ["SoundZone"] = "",
            ["ElectricalGrid"] = ""
        };
        if (omitted.Length > 0) fields.Remove(omitted);
        var type = edit ? typeof(CampEditViewModel) : typeof(CampRegisterViewModel);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(type);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = type
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = (CampRegisterViewModel)binding.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);

        var result = edit ? await controller.Edit(camp.Slug, (CampEditViewModel)model) : await controller.Register(model);
        var writes = _camps.ReceivedCalls().Where(call => call.GetMethodInfo().Name is "CreateCampAsync" or "UpdateCampAsync").ToList();
        if (omitted.Length > 0 || !string.Equals(memberCount, "1", StringComparison.Ordinal) || timesAtNowhere < 0)
        {
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
            var field = omitted.Length > 0 ? omitted : timesAtNowhere < 0 ? "TimesAtNowhere" : "MemberCount";
            controller.ModelState[field]!.Errors.Should().ContainSingle();
            writes.Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            controller.ModelState.IsValid.Should().BeTrue();
            var arguments = writes.Should().ContainSingle().Which.GetArguments();
            (edit ? ((CampUpdateInput)arguments[0]!).TimesAtNowhere : (int)arguments[7]!).Should().Be(timesAtNowhere);
            var data = edit ? ((CampUpdateInput)arguments[0]!).SeasonData : (CampSeasonData)arguments[8]!;
            data.AcceptingMembers.Should().Be(YesNoMaybe.Yes);
            data.KidsWelcome.Should().Be(YesNoMaybe.Yes);
            data.KidsVisiting.Should().Be((KidsVisitingPolicy)0);
            data.HasPerformanceSpace.Should().Be((PerformanceSpaceStatus)0);
            data.AdultPlayspace.Should().Be((AdultPlayspacePolicy)0);
            data.SpaceRequirement.Should().BeNull();
            data.SoundZone.Should().BeNull();
            data.ElectricalGrid.Should().BeNull();
        }
    }

    [HumansTheory]
    [InlineData("Contact", false)]
    [InlineData("Contact", true)]
    [InlineData("Request", false)]
    [InlineData("Request", true)]
    [InlineData("Withdraw", false)]
    [InlineData("Withdraw", true)]
    [InlineData("Leave", false)]
    [InlineData("Leave", true)]
    public async Task MemberPreflight_CancelsCampAndAccountReadsBeforeMutation(string action, bool cancelAtAccount)
    {
        using var request = new CancellationTokenSource();
        var actorId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha", CampSeasonStatus.Active);
        var abort = false;
        async Task<CampInfo?> ReadCampAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (abort && cancelAtAccount) await request.CancelAsync();
            return camp;
        }
        async ValueTask<UserInfo?> ReadActorAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return MakeUserInfo(actorId);
        }
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>())
            .Returns(call => ReadCampAsync(call.Arg<CancellationToken>()));
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(Settings2026());
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(call => ReadActorAsync(call.Arg<CancellationToken>()));
        _campsLocalizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        _camps.RequestCampMembershipAsync(camp.Id, actorId, Arg.Any<CancellationToken>())
            .Returns(new CampMemberRequestResult(Guid.NewGuid(), default, "Requested", CampMemberRequestNoticeLevel.Success));
        var memberId = Guid.NewGuid();
        _camps.WithdrawCampMembershipRequestAsync(memberId, actorId, Arg.Any<CancellationToken>())
            .Returns(CampMembershipMutationResult.Success());
        _camps.LeaveCampAsync(memberId, actorId, Arg.Any<CancellationToken>())
            .Returns(CampMembershipMutationResult.Success());
        _contacts.SendFacilitatedMessageAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<string>()).Returns(new CampContactResult(true, false));
        var controller = BuildController(actorId);
        controller.HttpContext.RequestAborted = request.Token;
        Task<IActionResult> InvokeAsync() => action switch
        {
            "Contact" => controller.Contact(camp.Slug, new CampContactViewModel { Message = "Hello" }),
            "Request" => controller.RequestMembership(camp.Slug),
            "Withdraw" => controller.WithdrawMembershipRequest(camp.Slug, memberId),
            _ => controller.LeaveMembership(camp.Slug, memberId),
        };
        Assert.IsType<RedirectToActionResult>(await InvokeAsync());
        var mutation = Assert.Single(_camps.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name is not
                (nameof(ICampManagement.GetCampBySlugAsync) or nameof(ICampManagement.GetSettingsAsync)))
            .Concat(_contacts.ReceivedCalls()));
        foreach (var token in mutation.GetArguments().OfType<CancellationToken>())
            Assert.Equal(CancellationToken.None, token);
        _camps.ClearReceivedCalls();
        _contacts.ClearReceivedCalls();
        _users.ClearReceivedCalls();
        abort = true;
        if (!cancelAtAccount) await request.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(InvokeAsync);
        Assert.Empty(_contacts.ReceivedCalls());
        Assert.All(_camps.ReceivedCalls(), call => Assert.Equal(nameof(ICampManagement.GetCampBySlugAsync), call.GetMethodInfo().Name));
        await _camps.Received(1).GetCampBySlugAsync(camp.Slug, request.Token);
        if (cancelAtAccount) await _users.Received(1).GetUserInfoAsync(actorId, request.Token);
        else Assert.Empty(_users.ReceivedCalls());
    }

    [HumansTheory]
    [InlineData("Edit")]
    [InlineData("OptIn")]
    [InlineData("Withdraw")]
    [InlineData("MarkFull")]
    [InlineData("UploadImage")]
    [InlineData("DeleteImage")]
    public async Task CampWrite_PassesTheResolvedActor(string action)
    {
        var actorId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: actorId);
        var season = camp.Seasons.Single();
        var imageId = Guid.NewGuid();
        const string key = "Camps_Flash_CampNotFound";
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUserInfo(actorId)));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _campsLocalizer[Arg.Any<string>()].Returns(c => new LocalizedString(c.Arg<string>(), c.Arg<string>()));
        _campsLocalizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(c => new LocalizedString(c.ArgAt<string>(0), c.ArgAt<string>(0)));
        _camps.UpdateCampAsync(Arg.Any<CampUpdateInput>(), actorId).Returns(CampUpdateResult.Failure(key));
        _camps.OptInToSeasonAsync(camp.Id, Event2027.EventSettingsId, actorId).Returns(new CampWriteResult<CampSeason>(null, key));
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(Settings2026());
        _camps.WithdrawSeasonAsync(camp.Id, season.Id, actorId).Returns(CampUpdateResult.Failure(key));
        _camps.MarkSeasonFullAsync(camp.Id, season.Id, actorId).Returns(CampUpdateResult.Failure(key));
        _camps.UploadImageAsync(camp.Id, Arg.Any<Stream>(), "camp.jpg", "image/jpeg", 1, actorId)
            .Returns(CampImageUploadResult.Failure(key));
        _camps.DeleteImageAsync(camp.Id, imageId, actorId).Returns(CampUpdateResult.Failure(key));
        var controller = BuildController(actorId);
        using var image = new MemoryStream([1]);
        var file = new FormFile(image, 0, 1, "file", "camp.jpg")
        { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };

        switch (action)
        {
            case "Edit":
                await controller.Edit(camp.Slug, new CampEditViewModel
                { CampId = camp.Id, SeasonId = season.Id, Name = "Alpha", ContactPhone = "+34600000000" });
                await _camps.Received(1).UpdateCampAsync(Arg.Any<CampUpdateInput>(), actorId); break;
            case "OptIn":
                await controller.OptIn(camp.Slug, Event2027.EventSettingsId);
                await _camps.Received(1).OptInToSeasonAsync(camp.Id, Event2027.EventSettingsId, actorId); break;
            case "Withdraw":
                await controller.Withdraw(camp.Slug, season.Id);
                await _camps.Received(1).WithdrawSeasonAsync(camp.Id, season.Id, actorId); break;
            case "MarkFull":
                await controller.MarkFull(camp.Slug, season.Id);
                await _camps.Received(1).MarkSeasonFullAsync(camp.Id, season.Id, actorId); break;
            case "UploadImage":
                await controller.UploadImage(camp.Slug, file);
                await _camps.Received(1).UploadImageAsync(camp.Id, Arg.Any<Stream>(), "camp.jpg", "image/jpeg", 1, actorId); break;
            case "DeleteImage":
                await controller.DeleteImage(camp.Slug, imageId);
                await _camps.Received(1).DeleteImageAsync(camp.Id, imageId, actorId); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Register_stops_loading_when_the_request_is_abandoned(bool cancelAfterSettings, bool invalidPost)
    {
        using var request = new CancellationTokenSource();
        var abandon = false;
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            if (abandon && cancelAfterSettings) await request.CancelAsync();
            return Settings2026();
        });
        _cityPlanning.GetRegistrationInfoAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return "Registration instructions";
        });
        var controller = BuildController(Guid.NewGuid());
        controller.HttpContext.RequestAborted = request.Token;
        if (invalidPost) controller.ModelState.AddModelError("Name", "Required");
        Func<Task<IActionResult>> load = invalidPost
            ? () => controller.Register(new CampRegisterViewModel())
            : controller.Register;
        (await load()).Should().BeOfType<ViewResult>();
        controller.ViewData["SeasonYear"].Should().Be(2026);
        controller.ViewData["RegistrationInfo"].Should().Be("Registration instructions");

        abandon = true;
        if (!cancelAfterSettings) await request.CancelAsync();
        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData("Register", "Viewer")]
    [InlineData("Register", "Settings")]
    [InlineData("OptIn", "Settings")]
    public async Task RegistrationPreflight_CancelsBeforeAnyWrite(string route, string boundary)
    {
        using var request = new CancellationTokenSource();
        await request.CancelAsync();
        var userId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha", CampSeasonStatus.Active, userId);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (string.Equals(boundary, "Viewer", StringComparison.Ordinal)) call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(MakeUserInfo(userId));
        });
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Settings2026();
        });
        _campsLocalizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(new LocalizedString("Refused", "Refused"));
        _camps.CreateCampAsync(default, default!, default!, default!, null, null, false, 0, default!, null, Guid.Empty)
            .ReturnsForAnyArgs(new CampWriteResult<Camp>(null, "Refused"));
        _camps.OptInToSeasonAsync(camp.Id, Event2027.EventSettingsId, userId)
            .Returns(new CampWriteResult<CampSeason>(null, "Refused"));
        var controller = BuildController(userId);
        controller.HttpContext.RequestAborted = request.Token;
        Func<Task<IActionResult>> action = string.Equals(route, "Register", StringComparison.Ordinal)
            ? () => controller.Register(new CampRegisterViewModel { Name = "New camp" })
            : () => controller.OptIn(camp.Slug, Event2027.EventSettingsId);

        await action.Should().ThrowAsync<OperationCanceledException>();

        _camps.ReceivedCalls().Select(call => call.GetMethodInfo().Name).Should()
            .NotContain(nameof(ICampManagement.CreateCampAsync)).And.NotContain(nameof(ICampManagement.OptInToSeasonAsync));
    }

    [HumansTheory]
    [InlineData(nameof(CampController.Edit), "Camp")]
    [InlineData(nameof(CampController.Edit), "Viewer")]
    [InlineData(nameof(CampController.Edit), "EditData")]
    [InlineData(nameof(CampController.Members), "Camp")]
    [InlineData(nameof(CampController.Members), "Viewer")]
    [InlineData(nameof(CampController.Members), "EditData")]
    [InlineData(nameof(CampController.Contact), "Camp")]
    public async Task CampReadPages_StopLoadingAfterRequestCancellation(string route, string boundary)
    {
        using var request = new CancellationTokenSource();
        var abandon = false;
        var boundaryToken = CancellationToken.None;
        async Task<T> ReadAsync<T>(T value, string name, CancellationToken ct)
        {
            if (abandon && string.Equals(name, boundary, StringComparison.Ordinal))
            {
                await request.CancelAsync();
                boundaryToken = ct;
            }
            ct.ThrowIfCancellationRequested();
            return value;
        }

        var userId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha", CampSeasonStatus.Active, userId);
        var season = camp.Seasons.Single();
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<CampInfo?>(camp, "Camp", call.Arg<CancellationToken>()));
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
            new ValueTask<UserInfo?>(ReadAsync<UserInfo?>(MakeUserInfo(userId), "Viewer", call.Arg<CancellationToken>())));
        _camps.GetCampEditDataAsync(camp.Id, null, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<CampEditData?>(MakeEditData(camp, season), "EditData", call.Arg<CancellationToken>()));
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(Settings2026());
        _authorization.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _roles.BuildPanelAsync(season.Id, Arg.Any<CancellationToken>())
            .Returns(new CampRolesPanelData(season.Id, []));
        var controller = BuildController(userId);
        controller.HttpContext.RequestAborted = request.Token;
        Func<Task<IActionResult>> load = route switch
        {
            nameof(CampController.Edit) => () => controller.Edit(camp.Slug, null, request.Token),
            nameof(CampController.Members) => () => controller.Members(camp.Slug, null, request.Token),
            nameof(CampController.Contact) => () => controller.Contact(camp.Slug),
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };

        (await load()).Should().BeOfType<ViewResult>();
        abandon = true;
        await load.Should().ThrowAsync<OperationCanceledException>();
        boundaryToken.Should().Be(request.Token);
    }

    [HumansFact]
    public void UploadImage_allows_one_valid_image_without_accepting_a_larger_request()
    {
        var limit = typeof(CampController).GetMethod(nameof(CampController.UploadImage))!
            .GetCustomAttribute<RequestSizeLimitAttribute>();

        limit.Should().NotBeNull();
        typeof(RequestSizeLimitAttribute)
            .GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(limit)
            .Should().Be(11L * 1024 * 1024);
    }

    [HumansFact]
    public async Task CampForms_ValidateExistingFieldLimitsBeforeWriting()
    {
        using var cultureScope = new CultureScope("en");
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var shared = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var userId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: userId);
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(Settings2026());
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        foreach (var edit in new[] { false, true })
        {
            var model = edit ? new CampEditViewModel() : new CampRegisterViewModel();
            model.TimesAtNowhere = -1;
            // MVC converts an empty posted email field to null before validation.
            model.ContactEmail = null!;
            var controller = BuildController(userId);
            controller.HttpContext.RequestServices = services;
            validator.Validate(controller.ControllerContext, null, "", model);
            foreach (var field in new[] { "Name", "ContactEmail", "BlurbLong", "BlurbShort", "Languages" })
            {
                controller.ModelState.Should().ContainKey(field);
                controller.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage
                    .Should().Be(shared["Validation_Required"].Value);
            }

            controller.ModelState.Clear();
            model.Name = new string('n', 257);
            model.ContactEmail = new string('a', 245) + "@example.com";
            model.ContactPhone = "+" + new string('1', 64);
            model.BlurbLong = new string('x', 4001);
            model.BlurbShort = new string('x', 1001);
            model.Languages = new string('x', 257);
            model.KidsAreaDescription = new string('x', 2001);
            model.PerformanceTypes = new string('x', 1001);
            validator.Validate(controller.ControllerContext, null, "", model);
            foreach (var (field, max) in new[]
            {
                ("Name", 256), ("ContactEmail", 256), ("ContactPhone", 64), ("BlurbLong", 4000),
                ("BlurbShort", 1000), ("Languages", 256), ("KidsAreaDescription", 2000), ("PerformanceTypes", 1000)
            })
            {
                controller.ModelState.Should().ContainKey(field);
                controller.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage
                    .Should().Be(shared["Validation_MaxLength", field, max].Value);
            }

            var result = edit ? await controller.Edit(camp.Slug, (CampEditViewModel)model) : await controller.Register(model);
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);

            controller.ModelState["MemberCount"]!.Errors.Should().ContainSingle().Which.ErrorMessage
                .Should().Be(shared["Validation_Range", "MemberCount", 1, int.MaxValue].Value);

            controller.ModelState["TimesAtNowhere"]!.Errors.Should().ContainSingle().Which.ErrorMessage
                .Should().Be(shared["Validation_Range", "TimesAtNowhere", 0, int.MaxValue].Value);

            controller.ModelState.Clear();
            model.TimesAtNowhere = 0;
            model.MemberCount = 1;
            model.Name = new string('n', 256);
            model.ContactEmail = new string('a', 244) + "@example.com";
            model.ContactPhone = "+" + new string('1', 63);
            model.BlurbLong = new string('x', 4000);
            model.BlurbShort = new string('x', 1000);
            model.Languages = new string('x', 256);
            model.KidsAreaDescription = new string('x', 2000);
            model.PerformanceTypes = new string('x', 1000);
            validator.Validate(controller.ControllerContext, null, "", model);
            controller.ModelState.IsValid.Should().BeTrue();

            controller.ModelState.Clear();
            model.ContactEmail = "invalid email";
            validator.Validate(controller.ControllerContext, null, "", model);
            controller.ModelState.Should().ContainKey("ContactEmail");
            controller.ModelState["ContactEmail"]!.Errors.Should().ContainSingle().Which.ErrorMessage
                .Should().Be(shared["Validation_EmailAddress"].Value);
        }
        _camps.ReceivedCalls().Should().NotContain(call =>
            call.GetMethodInfo().Name == "CreateCampAsync" || call.GetMethodInfo().Name == "UpdateCampAsync");
    }

    [HumansFact]
    public async Task RegistrationAndRenewal_LocalizeRuleFailures()
    {
        using var cultureScope = new CultureScope("en");
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<CampsResource>>();
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(Settings2026());
        var reservedKey = "Camps_Flash_ReservedName";
        _camps.CreateCampAsync(default, default!, default!, default!, null, null, false, 0,
                default!, null, Guid.Empty)
            .ReturnsForAnyArgs(new CampWriteResult<Camp>(null, reservedKey));
        var controller = BuildController(userId, localizer);
        var model = new CampRegisterViewModel { Name = "Register" };

        (await controller.Register(model)).Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        var expected = localizer[reservedKey, model.Name];
        expected.ResourceNotFound.Should().BeFalse();
        controller.ModelState[string.Empty]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);

        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: userId);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        foreach (var key in new[]
        {
            "Camps_Flash_SeasonNotOpen", "Camps_Flash_SeasonAlreadyExists", "Camps_Flash_NoPreviousSeason"
        })
        {
            _camps.OptInToSeasonAsync(camp.Id, Event2027.EventSettingsId, actorUserId: Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(new CampWriteResult<CampSeason>(null, key));
            (await controller.OptIn(camp.Slug, Event2027.EventSettingsId)).Should().BeOfType<RedirectToActionResult>();
            expected = localizer[key, Event2027.EventName];
            expected.ResourceNotFound.Should().BeFalse();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(expected.Value);
        }
    }

    [HumansFact]
    public async Task Register_LocalizesThePhoneFieldInValidationErrors()
    {
        using var cultureScope = new CultureScope("en");
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var campsLocalizer = services.GetRequiredService<IStringLocalizer<CampsResource>>();
        var sharedLocalizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Settings2026());
        var controller = BuildController(Guid.NewGuid(), campsLocalizer, sharedLocalizer);
        var model = new CampRegisterViewModel { ContactPhone = "612 345 678" };

        var result = await controller.Register(model);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        var label = campsLocalizer["Camps_ContactPhoneLabel"];
        label.ResourceNotFound.Should().BeFalse();
        var expected = sharedLocalizer["Validation_PhoneE164", label.Value];
        expected.ResourceNotFound.Should().BeFalse();
        controller.ModelState[nameof(model.ContactPhone)]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(expected.Value);
        _camps.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "CreateCampAsync");
    }

    [HumansFact]
    public async Task UploadImage_LocalizesValidationFailures()
    {
        using var cultureScope = new CultureScope("en");
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<CampsResource>>();
        var userId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: userId);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var controller = BuildController(userId, localizer);
        using var content = new MemoryStream([1]);
        var file = new FormFile(content, 0, 1, "file", "camp.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };
        foreach (var key in new[]
        {
            "Camps_Validation_ImageCount", "Camps_Validation_ImageType", "Camps_Validation_ImageSize",
            "Camps_Validation_ImageFilenameLength", "Camps_Validation_ImageExtension"
        })
        {
            _camps.UploadImageAsync(camp.Id, Arg.Any<Stream>(), "camp.jpg", "image/jpeg", 1, actorUserId: Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(CampImageUploadResult.Failure(key));

            await controller.UploadImage(camp.Slug, file);

            var expected = localizer[key];
            expected.ResourceNotFound.Should().BeFalse();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(expected.Value);
        }
    }

    [HumansFact]
    public async Task SelfMembershipActions_LocalizeRuleFailures()
    {
        using var cultureScope = new CultureScope("en");
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<CampsResource>>();
        var userId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        var controller = BuildController(userId, localizer);

        foreach (var key in new[] { "Camps_Flash_RoleMemberNotFound", "Camps_Flash_LeaveRequiresActive" })
        {
            _camps.LeaveCampAsync(memberId, userId, Arg.Any<CancellationToken>())
                .Returns(CampMembershipMutationResult.Failure(key));
            await controller.LeaveMembership(camp.Slug, memberId);
            var expected = localizer[key];
            expected.ResourceNotFound.Should().BeFalse();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(expected.Value);
        }
        foreach (var key in new[] { "Camps_Flash_RoleMemberNotFound", "Camps_Flash_WithdrawRequiresPending" })
        {
            _camps.WithdrawCampMembershipRequestAsync(memberId, userId, Arg.Any<CancellationToken>())
                .Returns(CampMembershipMutationResult.Failure(key));
            await controller.WithdrawMembershipRequest(camp.Slug, memberId);
            var expected = localizer[key];
            expected.ResourceNotFound.Should().BeFalse();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(expected.Value);
        }
    }

    [HumansFact]
    public async Task LeadActions_LocalizeGuardFailures()
    {
        using var cultureScope = new CultureScope("en");
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<CampsResource>>();
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: actorId);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUserInfo(actorId)));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var logger = Substitute.For<ILogger<CampController>>();
        var controller = BuildController(actorId, localizer, logger: logger);
        foreach (var (action, key) in new[]
        {
            ("Approve", "Camps_Flash_ApproveRequiresPending"),
            ("Reject", "Camps_Flash_RejectRequiresPending"),
            ("Remove", "Camps_Flash_RemoveRequiresActive"),
            ("Approve", "Camps_Flash_RoleMemberNotFound"),
            ("Reject", "Camps_Flash_RoleMemberNotFound"),
            ("Remove", "Camps_Flash_RoleMemberNotFound"),
            ("AddHistoricalName", "Camps_Flash_NameRequired"),
            ("AddHistoricalName", "Camps_Validation_HistoricalNameLength"),
            ("RemoveHistoricalName", "Camps_Flash_CampNotFound"),
            ("RemoveHistoricalName", "Camps_Flash_HistoricalNameNotFound"),
            ("RemoveHistoricalName", "Camps_Flash_HistoricalNameWrongCamp"),
            ("DeleteImage", "Camps_Flash_ImageNotFound"),
            ("DeleteImage", "Camps_Flash_ImageWrongCamp"),
        })
        {
            logger.ClearReceivedCalls();
            Task<IActionResult> result;
            switch (action)
            {
                case "Approve":
                    _camps.ApproveCampMemberAsync(camp.Id, targetId, actorId).Returns(CampMembershipMutationResult.Failure(key));
                    result = controller.ApproveMembership(camp.Slug, targetId); break;
                case "Reject":
                    _camps.RejectCampMemberAsync(camp.Id, targetId, actorId).Returns(CampMembershipMutationResult.Failure(key));
                    result = controller.RejectMembership(camp.Slug, targetId); break;
                case "Remove":
                    _camps.RemoveCampMemberAsync(camp.Id, targetId, actorId).Returns(CampMembershipMutationResult.Failure(key));
                    result = controller.RemoveMembership(camp.Slug, targetId); break;
                case "AddHistoricalName":
                    _camps.AddHistoricalNameAsync(camp.Id, "Previous Name", actorId).Returns(CampUpdateResult.Failure(key));
                    result = controller.AddHistoricalName(camp.Slug, "Previous Name"); break;
                case "RemoveHistoricalName":
                    _camps.RemoveHistoricalNameAsync(camp.Id, targetId, actorId).Returns(CampUpdateResult.Failure(key));
                    result = controller.RemoveHistoricalName(camp.Slug, targetId); break;
                case "DeleteImage":
                    _camps.DeleteImageAsync(camp.Id, targetId, actorUserId: Arg.Any<Guid>()).Returns(CampUpdateResult.Failure(key));
                    result = controller.DeleteImage(camp.Slug, targetId); break;
                default: throw new InvalidOperationException("Unknown test action");
            }
            (await result).Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(action is "AddHistoricalName" or "RemoveHistoricalName" or "DeleteImage" ? "Edit" : "Members");
            var expected = localizer[key];
            expected.ResourceNotFound.Should().BeFalse();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(expected.Value);
            var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
            args[0].Should().Be(LogLevel.Warning);
            args[3].Should().BeNull();
            args[2]!.ToString().Should().Contain(camp.Id.ToString()).And.Contain(key);
            if (!string.Equals(action, "AddHistoricalName", StringComparison.Ordinal))
                args[2]!.ToString().Should().Contain(targetId.ToString());
        }
    }

    [HumansTheory]
    [InlineData("Approve", false)]
    [InlineData("Reject", false)]
    [InlineData("Remove", false)]
    [InlineData("Withdraw", false)]
    [InlineData("Approve", true)]
    [InlineData("Reject", true)]
    [InlineData("Remove", true)]
    [InlineData("Withdraw", true)]
    public async Task MembershipAction_LogsExpectedRejectionAndPropagatesUnexpectedFailure(string action, bool unexpected)
    {
        var actorId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, leadUserId: actorId);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUserInfo(actorId)));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        const string reason = "Camps_Flash_RoleMemberNotFound";
        _campsLocalizer[reason].Returns(new LocalizedString(reason, "Member not found"));
        var failure = new InvalidOperationException("Storage unavailable");
        switch (action)
        {
            case "Approve": if (unexpected) _camps.ApproveCampMemberAsync(camp.Id, memberId, actorId).ThrowsAsync(failure); else _camps.ApproveCampMemberAsync(camp.Id, memberId, actorId).Returns(CampMembershipMutationResult.Failure(reason)); break;
            case "Reject": if (unexpected) _camps.RejectCampMemberAsync(camp.Id, memberId, actorId).ThrowsAsync(failure); else _camps.RejectCampMemberAsync(camp.Id, memberId, actorId).Returns(CampMembershipMutationResult.Failure(reason)); break;
            case "Remove": if (unexpected) _camps.RemoveCampMemberAsync(camp.Id, memberId, actorId).ThrowsAsync(failure); else _camps.RemoveCampMemberAsync(camp.Id, memberId, actorId).Returns(CampMembershipMutationResult.Failure(reason)); break;
            case "Withdraw": if (unexpected) _camps.WithdrawCampMembershipRequestAsync(memberId, actorId).ThrowsAsync(failure); else _camps.WithdrawCampMembershipRequestAsync(memberId, actorId).Returns(CampMembershipMutationResult.Failure(reason)); break;
        }
        var logger = Substitute.For<ILogger<CampController>>();
        var controller = BuildController(actorId, logger: logger);
        Func<Task<IActionResult>> act = action switch
        {
            "Approve" => () => controller.ApproveMembership(camp.Slug, memberId),
            "Reject" => () => controller.RejectMembership(camp.Slug, memberId),
            "Remove" => () => controller.RemoveMembership(camp.Slug, memberId),
            "Withdraw" => () => controller.WithdrawMembershipRequest(camp.Slug, memberId),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        if (unexpected)
        {
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Storage unavailable");
            logger.ReceivedCalls().Should().BeEmpty();
            return;
        }
        var result = (await act()).Should().BeOfType<RedirectToActionResult>().Subject;
        result.ActionName.Should().Be(action is "Withdraw" ? "Details" : "Members");
        result.RouteValues!["slug"].Should().Be(camp.Slug);
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("Member not found");
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Warning);
        args[3].Should().BeNull();
        args[2]!.ToString().Should().Contain(memberId.ToString()).And.Contain(reason);
        args[2]!.ToString().Should().Contain((action is "Withdraw" ? actorId : camp.Id).ToString());
    }

    [HumansFact]
    public void ContactForm_LocalizesRequiredAndLengthMessages()
    {
        using var cultureScope = new CultureScope("en");
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        foreach (var (message, key, arguments) in new (string, string, object[])[]
        {
            ("", "Validation_Required", ["Message"]),
            (new string('x', 2001), "Validation_MaxLength", ["Message", 2000])
        })
        {
            var context = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
            validator.Validate(context, null, "", new CampContactViewModel { Message = message });
            var expected = localizer[key, arguments];
            expected.ResourceNotFound.Should().BeFalse();
            context.ModelState["Message"]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);
        }
    }

    [HumansFact]
    public async Task Index_BuildsPublicDirectory_FromCachedCampInfoRead()
    {
        var matching = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, kidsWelcome: YesNoMaybe.Yes) with
        {
            Images = [new(Guid.NewGuid(), "/later.jpg", 3), new(Guid.NewGuid(), "/cover.jpg", 1)],
        };
        var filteredOut = MakeCamp("zeta", "Zeta Camp", CampSeasonStatus.Full, kidsWelcome: YesNoMaybe.No);
        var pending = MakeCamp("pending", "Pending Camp", CampSeasonStatus.Pending);
        StubCampReadModel([matching, filteredOut, pending]);
        _roles.GetDirectoryRoleSummariesAsync(EventIdFor(2026), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<CampDirectoryRoleSummary>>
            {
                [matching.Seasons.Single().Id] = [
                    new("Zebra", 2, 3) { SortOrder = 1 },
                    new("Alpha", 0, 1) { SortOrder = 1 },
                    new("Priority", 1, 1) { SortOrder = -1 },
                ],
            });
        var controller = BuildController();

        var result = await controller.Index(new CampFilterViewModel { KidsFriendly = true, ShowLeadPositions = true }, TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampIndexViewModel>().Subject;
        vm.Year.Should().Be(2026);
        vm.Camps.Should().ContainSingle(c => c.Id == matching.Id);
        vm.Camps.Single().ImageUrl.Should().Be("/cover.jpg");
        vm.Camps.Single().LeadPositionPills.Select(pill => pill.Name).Should().Equal("Priority", "Alpha", "Zebra");
        vm.MyCamps.Should().BeEmpty();
        ((int)controller.ViewBag.PendingCount).Should().Be(1);
        await _camps.Received(1).GetSettingsAsync(Arg.Any<CancellationToken>());
        await _camps.Received(1).GetCampsForEventAsync(EventIdFor(2026), Arg.Any<CancellationToken>());

        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([matching]);
        var apiResult = await new CampApiController(_camps).GetCamps(2026, TestContext.Current.CancellationToken);
        apiResult.Should().BeOfType<OkObjectResult>().Which.Value.Should()
            .BeAssignableTo<IReadOnlyList<CampPublicSummary>>().Which.Single().ImageUrl.Should().Be("/cover.jpg");
    }

    [HumansFact]
    public async Task Index_InvalidFilterQueryValue_ClearsModelStateAndRendersWithDefaults()
    {
        // Regression: a scanner payload in a bool
        // filter param leaves an invalid attempted value in ModelState, which the
        // checkbox tag helper would throw a FormatException re-converting at render.
        StubCampReadModel([]);
        var controller = BuildController();
        const string garbage = "test\")))EXTRACTVALUE(7966,...)";
        controller.ModelState.SetModelValue(
            "ShowLeadPositions", new ValueProviderResult(garbage));
        controller.ModelState.AddModelError(
            "ShowLeadPositions", $"The value '{garbage}' is not valid for ShowLeadPositions.");

        var result = await controller.Index(new CampFilterViewModel(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>();
        controller.ModelState.Should().BeEmpty(
            "the invalid attempted value must not survive to view rendering");
    }

    [HumansFact]
    public async Task Index_RoleLeadPendingCamp_AppearsInMyCamps_FromCampInfo()
    {
        var userId = Guid.NewGuid();
        var pending = MakeCamp("pending", "Pending Camp", CampSeasonStatus.Pending, leadUserId: userId);
        StubCampReadModel([pending]);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        var controller = BuildController(userId);

        var result = await controller.Index(null, TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampIndexViewModel>().Subject;
        vm.Camps.Should().BeEmpty();
        vm.MyCamps.Should().ContainSingle(c => c.Id == pending.Id);
        ((int)controller.ViewBag.PendingCount).Should().Be(1);
    }

    [HumansTheory]
    [InlineData(nameof(CampMemberRequestOutcome.Created), nameof(CampMemberRequestNoticeLevel.Success), "Camps_Flash_RequestCreated", TempDataKeys.SuccessMessage)]
    [InlineData(nameof(CampMemberRequestOutcome.AlreadyActive), nameof(CampMemberRequestNoticeLevel.Info), "Camps_Flash_RequestAlreadyActive", TempDataKeys.InfoMessage)]
    [InlineData(nameof(CampMemberRequestOutcome.AlreadyPending), nameof(CampMemberRequestNoticeLevel.Info), "Camps_Flash_RequestAlreadyPending", TempDataKeys.InfoMessage)]
    [InlineData(nameof(CampMemberRequestOutcome.NoOpenSeason), nameof(CampMemberRequestNoticeLevel.Error), "Camps_Flash_RequestNoOpenSeason", TempDataKeys.ErrorMessage)]
    public async Task RequestMembership_RendersLocalizedOutcome(
        string outcome, string noticeLevel, string messageKey, string tempDataKey)
    {
        var userId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        _camps.RequestCampMembershipAsync(camp.Id, userId, Arg.Any<CancellationToken>())
            .Returns(new CampMemberRequestResult(Guid.NewGuid(), Enum.Parse<CampMemberRequestOutcome>(outcome), messageKey, Enum.Parse<CampMemberRequestNoticeLevel>(noticeLevel)));
        const string translated = "Solicitud de incorporación traducida";
        _campsLocalizer[messageKey].Returns(new LocalizedString(messageKey, translated));
        var controller = BuildController(userId);

        var result = await controller.RequestMembership(camp.Slug);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(CampController.Details));
        redirect.RouteValues!["slug"].Should().Be(camp.Slug);
        controller.TempData[tempDataKey].Should().Be(translated);
    }

    [HumansTheory]
    [InlineData(null, false, true)]
    [InlineData(false, true, true)]
    [InlineData(null, false, false)]
    [InlineData(false, true, false)]
    public async Task EarlyEntry_MissingOrMalformedDecision_DoesNotWrite(bool? granted, bool malformed, bool canManage)
    {
        var ct = TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(actorId));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(canManage ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        _campsLocalizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        _camps.SetEarlyEntryAsync(camp.Id, Arg.Any<Guid>(), Arg.Any<bool>(), actorId, ct)
            .Returns(SetEarlyEntryOutcome.Success);
        var controller = BuildController(actorId);
        if (malformed)
            controller.ModelState.AddModelError("granted", "Not a Boolean");

        var result = await controller.SetMemberEarlyEntry(camp.Slug, Guid.NewGuid(), granted, ct);

        if (canManage)
            result.Should().BeOfType<BadRequestObjectResult>();
        else
            result.Should().BeOfType<ForbidResult>();
        await _camps.DidNotReceiveWithAnyArgs().SetEarlyEntryAsync(default, default, default, default);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EarlyEntry_ExplicitDecision_IsPreserved(bool granted)
    {
        var ct = TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(actorId));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _campsLocalizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        _camps.SetEarlyEntryAsync(camp.Id, memberId, granted, actorId, ct).Returns(SetEarlyEntryOutcome.Success);

        var result = await BuildController(actorId).SetMemberEarlyEntry(camp.Slug, memberId, granted, ct);

        result.Should().BeOfType<RedirectToActionResult>();
        await _camps.Received(1).SetEarlyEntryAsync(camp.Id, memberId, granted, actorId, ct);
    }

    [HumansTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task MemberShortcuts_MalformedEvent_DoesNotWrite(bool assignRole, bool canManage)
    {
        var ct = TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Full, year: 2029);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(actorId));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(canManage ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        _campsLocalizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = BuildController(actorId);
        controller.ModelState.AddModelError("eventSettingsId", "Not an event id");
        var userId = Guid.NewGuid();

        var result = assignRole
            ? await controller.AssignRoleByUser(camp.Slug, Guid.NewGuid(), userId, ct, eventSettingsId: null)
            : await controller.AddMember(camp.Slug, userId, ct, eventSettingsId: null);

        if (canManage)
            result.Should().BeOfType<BadRequestObjectResult>();
        else
            result.Should().BeOfType<ForbidResult>();
        await _camps.DidNotReceiveWithAnyArgs().AddCampMemberToActiveSeasonAsync(default, default, default);
        await _camps.DidNotReceiveWithAnyArgs().AddMemberAndAssignRoleInActiveSeasonAsync(default, default, default, default);
    }

    [HumansTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task MemberShortcuts_PreserveTheDisplayedEvent(bool assignRole, bool eventShown)
    {
        var ct = TestContext.Current.CancellationToken;
        Guid? eventSettingsId = eventShown ? Event2029.EventSettingsId : null;
        var actorId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Full, year: 2029);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(actorId));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _campsLocalizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var controller = BuildController(actorId);

        var result = assignRole
            ? await controller.AssignRoleByUser(camp.Slug, roleId, userId, ct, eventSettingsId)
            : await controller.AddMember(camp.Slug, userId, ct, eventSettingsId);

        result.Should().BeOfType<RedirectToActionResult>().Which.RouteValues!["eventSettingsId"].Should().Be(eventSettingsId);
        if (assignRole)
            await _camps.Received(1).AddMemberAndAssignRoleInActiveSeasonAsync(camp.Id, roleId, userId, actorId, ct, eventSettingsId);
        else
            await _camps.Received(1).AddCampMemberToActiveSeasonAsync(camp.Id, userId, actorId, ct, eventSettingsId);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignRole_UsesTheMembersOwnSeasonWithinTheCamp(bool foreignMember)
    {
        var ct = TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var member = MakeSeasonMember(Guid.NewGuid());
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        var first = camp.Seasons.Single();
        var target = first with
        {
            Id = Guid.NewGuid(),
            Year = 2027,
            EventSettingsId = Event2027.EventSettingsId,
            Members = foreignMember ? [] : [member]
        };
        camp = camp with { Seasons = [first, target] };
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(actorId));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _campsLocalizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var roleId = Guid.NewGuid();

        var result = await BuildController(actorId).AssignRole(camp.Slug, roleId, member.Id, ct);

        if (foreignMember)
            await _roles.DidNotReceiveWithAnyArgs().AssignAsync(default, default, default, default);
        else
        {
            await _roles.Received(1).AssignAsync(target.Id, roleId, member.Id, actorId, ct);
            result.Should().BeOfType<RedirectToActionResult>().Which.RouteValues!["eventSettingsId"].Should().Be(Event2027.EventSettingsId);
        }
    }

    [HumansFact]
    public async Task AddMember_PropagatesRequestCancellation()
    {
        var actorId = Guid.NewGuid();
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(camp));
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(actorId)));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _camps.AddCampMemberToActiveSeasonAsync(camp.Id, Arg.Any<Guid>(), actorId, cancellation.Token)
            .Returns(Task.FromCanceled<AddCampMemberOutcome>(cancellation.Token));

        var act = () => BuildController(actorId).AddMember(camp.Slug, Guid.NewGuid(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task Index_RoleLeadPublicCamp_IsPinnedBeforeAlphabeticalCamps()
    {
        var userId = Guid.NewGuid();
        var alphabeticalFirst = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        var leadCamp = MakeCamp("zeta", "Zeta Camp", CampSeasonStatus.Active, leadUserId: userId);
        StubCampReadModel([alphabeticalFirst, leadCamp]);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        var controller = BuildController(userId);

        var result = await controller.Index(null, TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampIndexViewModel>().Subject;
        vm.Camps.Select(c => c.Id).Should().Equal(leadCamp.Id, alphabeticalFirst.Id);
    }

    [HumansTheory]
    [InlineData(CampSeasonStatus.Active)]
    [InlineData(CampSeasonStatus.Full)]
    [InlineData(CampSeasonStatus.Pending)]
    [InlineData(CampSeasonStatus.Rejected)]
    public async Task Members_RolePanel_UsesTheSelectedSeason(CampSeasonStatus status)
    {
        var leadId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var camp = MakeCamp("garden", "Selected event", status, leadId, members: [MakeSeasonMember(memberId)]);
        var selected = camp.Seasons.Single();
        var other = selected with
        {
            Id = Guid.NewGuid(), EventSettingsId = Guid.NewGuid(), Status = CampSeasonStatus.Active,
            Name = "Other event", Members = []
        };
        camp = camp with { Seasons = [other, selected] };
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _camps.GetCampEditDataAsync(camp.Id, selected.EventSettingsId, Arg.Any<CancellationToken>())
            .Returns(MakeEditData(camp, selected));
        _users.GetUserInfoAsync(leadId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(leadId));
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [memberId] = MakeUserInfo(memberId) });
        _shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ShiftUserSummary> { [memberId] = MakeShiftUserSummary(memberId) });
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>()).Returns(AuthorizationResult.Success());
        _roles.BuildPanelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new CampRolesPanelData(call.ArgAt<Guid>(0), []));

        var result = await BuildController(leadId).Members(camp.Slug, selected.EventSettingsId,
            TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<CampEditViewModel>().Subject;
        model.ActiveMembers.Should().ContainSingle().Which.UserId.Should().Be(memberId);
        if (selected.IsPublic)
        {
            model.RolesPanel.Should().NotBeNull();
            model.RolesPanel!.CampSeasonId.Should().Be(selected.Id);
            model.RolesPanel!.ActiveMembers.Should().ContainSingle().Which.UserId.Should().Be(memberId);
            await _roles.Received(1).BuildPanelAsync(selected.Id, TestContext.Current.CancellationToken);
        }
        else
        {
            model.RolesPanel.Should().BeNull();
            _roles.ReceivedCalls().Should().BeEmpty();
        }
    }

    [HumansFact]
    public async Task Members_Shows_Active_Event_Shift_Counts_From_Shift_View()
    {
        var leadUserId = Guid.NewGuid();
        var zeroShiftMemberId = Guid.NewGuid();
        var oneShiftMemberId = Guid.NewGuid();
        var fivePlusShiftMemberId = Guid.NewGuid();
        var earliest = Instant.FromUtc(2026, 1, 1, 0, 0);

        var members = new[]
        {
            MakeSeasonMember(zeroShiftMemberId) with { RequestedAt = earliest.Plus(Duration.FromDays(2)) },
            MakeSeasonMember(oneShiftMemberId) with { RequestedAt = earliest },
            MakeSeasonMember(fivePlusShiftMemberId) with { RequestedAt = earliest.Plus(Duration.FromDays(1)) },
        };
        var camp = MakeCamp("garden-of-joy", "Garden of Joy", CampSeasonStatus.Active, leadUserId, members: members);
        var season = camp.Seasons.Single();

        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(camp));
        _camps.GetCampEditDataAsync(camp.Id, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampEditData?>(MakeEditData(camp, season)));
        _users.GetUserInfoAsync(leadUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(leadUserId)));
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                call.ArgAt<IReadOnlyCollection<Guid>>(0).ToDictionary(id => id, MakeUserInfo)));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _roles.BuildPanelAsync(season.Id, Arg.Any<CancellationToken>())
            .Returns(new CampRolesPanelData(season.Id, [
                new(new CampRoleDefinition { Id = Guid.NewGuid(), Name = "Zebra", SortOrder = 1 }, [
                    new CampRolesPanelSlot(Guid.NewGuid(), members[0].Id, zeroShiftMemberId)
                    { AssignedAt = earliest.Plus(Duration.FromDays(1)) },
                    new CampRolesPanelSlot(Guid.NewGuid(), members[1].Id, oneShiftMemberId)
                    { AssignedAt = earliest },
                ], 0, false, 2),
                new(new CampRoleDefinition { Id = Guid.NewGuid(), Name = "Alpha", SortOrder = 1 }, [], 1, false, 0),
                new(new CampRoleDefinition { Id = Guid.NewGuid(), Name = "Priority", SortOrder = -1 }, [], 1, false, 0),
            ]));
        _shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, ShiftUserSummary>>(
                new Dictionary<Guid, ShiftUserSummary>
                {
                    [zeroShiftMemberId] = MakeShiftUserSummary(zeroShiftMemberId),
                    [oneShiftMemberId] = MakeShiftUserSummary(oneShiftMemberId, SignupStatus.Confirmed),
                    [fivePlusShiftMemberId] = MakeShiftUserSummary(
                        fivePlusShiftMemberId,
                        SignupStatus.Confirmed,
                        SignupStatus.Pending,
                        SignupStatus.Confirmed,
                        SignupStatus.Pending,
                        SignupStatus.Confirmed,
                        SignupStatus.Bailed),
                }));

        var controller = BuildController(leadUserId);

        var result = await controller.Members(camp.Slug, null, TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampEditViewModel>().Subject;
        vm.RolesPanel!.Rows.Select(row => row.Name).Should().Equal("Priority", "Alpha", "Zebra");
        vm.ActiveMembers.Select(m => m.UserId).Should().Equal(oneShiftMemberId, fivePlusShiftMemberId, zeroShiftMemberId);
        vm.RolesPanel.ActiveMembers.Select(m => m.UserId).Should().Equal(oneShiftMemberId, fivePlusShiftMemberId, zeroShiftMemberId);
        vm.RolesPanel.Rows.Single(row => string.Equals(row.Name, "Zebra", StringComparison.Ordinal)).FilledSlots.Select(s => s.UserId)
            .Should().Equal(oneShiftMemberId, zeroShiftMemberId);
        vm.ActiveMembers.Single(m => m.UserId == zeroShiftMemberId).EventShiftSignupCount.Should().Be(0);
        vm.ActiveMembers.Single(m => m.UserId == oneShiftMemberId).EventShiftSignupCount.Should().Be(1);
        vm.ActiveMembers.Single(m => m.UserId == fivePlusShiftMemberId).EventShiftSignupCount.Should().Be(5);
        vm.ActiveMembers.Single(m => m.UserId == fivePlusShiftMemberId).EventShiftSignupDisplay.Should().Be("5+");
    }

    [HumansFact]
    public async Task Details_NonPublicSeason_AnonymousViewer_IsRefused()
    {
        // Global search stops offering a camp once its public-year season leaves Active/Full, and
        // the id path deliberately does not, so /Camps/{slug} has to be the enforcement point.
        var pending = MakeCamp("pending-camp", "Pending Camp", CampSeasonStatus.Pending);
        StubCampReadModel([pending]);
        _camps.GetCampBySlugAsync(pending.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(pending));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await BuildController().Details(pending.Slug, TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task SeasonDetails_NonPublicSeason_AnonymousViewer_IsRefused()
    {
        // The same coverage for the arbitrary-year route.
        var withdrawn = MakeCamp("withdrawn-camp", "Withdrawn Camp", CampSeasonStatus.Withdrawn);
        StubCampReadModel([withdrawn]);
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await BuildController().SeasonDetails(withdrawn.Slug, EventIdFor(2026), TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Details_NonPublicSeason_Lead_StillRenders()
    {
        // The slug gate must not lock a lead out of their own Pending camp.
        var leadUserId = Guid.NewGuid();
        var pending = MakeCamp("pending-camp", "Pending Camp", CampSeasonStatus.Pending, leadUserId: leadUserId);
        StubCampReadModel([pending]);
        _camps.GetCampBySlugAsync(pending.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(pending));
        _users.GetUserInfoAsync(leadUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(leadUserId)));
        _cityPlanning.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CityPlanningSettingsDto?>(new CityPlanningSettingsDto(
                Guid.NewGuid(), 2026, BarrioPlacementState.Closed, null, null, null, null, null, null, null, null, null,
                null, null, null, false, null, null,
                Instant.FromUtc(2026, 1, 1, 0, 0))));
        _roles.BuildPanelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new CampRolesPanelData(call.ArgAt<Guid>(0), [])));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        var result = await BuildController(leadUserId).Details(pending.Slug, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>();
    }

    [HumansFact]
    public async Task Details_FutureSeasonOnlyCamp_Lead_StillRenders()
    {
        // A camp registered for an open future year has no PublicYear season, so the
        // public-year projection cannot answer "is this user its lead" — the slug gate
        // must authorize against the loaded camp, or it 404s the lead's own camp right
        // after registration.
        var leadUserId = Guid.NewGuid();
        var future = MakeCamp("future-camp", "Future Camp", CampSeasonStatus.Pending,
            leadUserId: leadUserId, year: 2027);
        StubCampReadModel([]); // the PublicYear (2026) projection does not contain the camp
        _camps.GetCampBySlugAsync(future.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(future));
        _users.GetUserInfoAsync(leadUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(leadUserId)));
        _cityPlanning.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CityPlanningSettingsDto?>(new CityPlanningSettingsDto(
                Guid.NewGuid(), 2026, BarrioPlacementState.Closed, null, null, null, null, null, null, null, null, null,
                null, null, null, false, null, null,
                Instant.FromUtc(2026, 1, 1, 0, 0))));
        _roles.BuildPanelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new CampRolesPanelData(call.ArgAt<Guid>(0), [])));
        // Succeed only for the loaded CampInfo resource — mirrors the real handler, which
        // sees the lead assignment on the camp itself but not through the id lookup.
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Is<object?>(resource => ReferenceEquals(resource, future)),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        var result = await BuildController(leadUserId).Details(future.Slug, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>(
            because: "the lead of a not-yet-public camp must reach their own camp page");
    }

    [HumansFact]
    public async Task SeasonDetails_OptedInRenewalSeason_PriorSeasonLead_StillRenders()
    {
        // OptInToSeasonAsync copies no lead assignments into the renewal season, so the
        // year projection for the new year carries no leads — viewer state must resolve
        // from the slug-loaded camp, whose earlier seasons carry them.
        var leadUserId = Guid.NewGuid();
        var renewal = MakeCamp("garden", "Garden Camp", CampSeasonStatus.Pending, year: 2027);
        var priorSeason = renewal.Seasons.Single() with
        {
            Id = Guid.NewGuid(),
            Year = 2026,
            Status = CampSeasonStatus.Active,
            LeadUserIds = [leadUserId]
        };
        var fullCamp = renewal with { Seasons = [priorSeason, renewal.Seasons.Single()] };
        StubCampReadModel([]);
        _camps.GetCampsForEventAsync(EventIdFor(2027), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CampInfo>>([renewal]));
        _camps.GetCampBySlugAsync(renewal.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(fullCamp));
        _users.GetUserInfoAsync(leadUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(leadUserId)));
        _cityPlanning.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CityPlanningSettingsDto?>(new CityPlanningSettingsDto(
                Guid.NewGuid(), 2026, BarrioPlacementState.Closed, null, null, null, null, null, null, null, null, null,
                null, null, null, false, null, null,
                Instant.FromUtc(2026, 1, 1, 0, 0))));
        _roles.BuildPanelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new CampRolesPanelData(call.ArgAt<Guid>(0), [])));
        // Succeed only for the slug-loaded camp — the year projection cannot answer the
        // lead check, exactly as the real handler behaves.
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Is<object?>(resource => ReferenceEquals(resource, fullCamp)),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        var result = await BuildController(leadUserId)
            .SeasonDetails(renewal.Slug, EventIdFor(2027), TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>(
            because: "a lead must reach their camp's not-yet-approved renewal season");
    }

    [HumansFact]
    public async Task Details_AnonymousRender_CarriesNoMemberOrEarlyEntryData()
    {
        // Behavioral pin for "membership/EE data never render publicly": walk the actual
        // anonymous view-model graph — a leak that is renamed or nested still shows up
        // as the member's id or an Ee*/EarlyEntry-named node somewhere in the graph.
        var memberUserId = Guid.NewGuid();
        var member = new CampSeasonMemberInfo(
            Guid.NewGuid(), memberUserId, CampMemberStatus.Active,
            Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0),
            HasEarlyEntry: true);
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, members: [member]) with
        {
            Images = [new(Guid.NewGuid(), "/later.jpg", 3), new(Guid.NewGuid(), "/cover.jpg", 1)],
        };
        StubCampReadModel([camp]);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(camp));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await BuildController().Details(camp.Slug, TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject.Model!;
        vm.Should().BeOfType<CampDetailViewModel>().Which.ImageUrls.Should().Equal("/cover.jpg", "/later.jpg");
        var leaves = new List<(string Path, object? Value)>();
        CollectLeaves(vm, "vm", leaves, []);

        leaves.Should().NotContain(
            l => Equals(l.Value, memberUserId) || memberUserId.ToString().Equals(l.Value as string, StringComparison.OrdinalIgnoreCase),
            because: "an anonymous detail render must not carry any member identity");
        leaves.Where(l => l.Path.Split('.', '[')
                .Any(seg => seg.StartsWith("Ee", StringComparison.Ordinal)
                            || seg.Contains("EarlyEntry", StringComparison.OrdinalIgnoreCase)))
            .Should().BeEmpty("Early Entry state is admin-only (issue #490, spec §4.4) and must not reach the public detail shape");
    }

    private static void CollectLeaves(object? obj, string path, List<(string Path, object? Value)> leaves, HashSet<object> seen)
    {
        if (obj is null)
        {
            return;
        }

        var type = obj.GetType();
        if (type.IsPrimitive || type.IsEnum || obj is string || obj is Guid || obj is decimal
            || type.Namespace?.StartsWith("NodaTime", StringComparison.Ordinal) == true)
        {
            leaves.Add((path, obj));
            return;
        }

        if (obj is System.Collections.IEnumerable enumerable)
        {
            var i = 0;
            foreach (var item in enumerable)
            {
                CollectLeaves(item, $"{path}[{i++}]", leaves, seen);
            }

            return;
        }

        if (!seen.Add(obj))
        {
            return;
        }

        foreach (var property in type.GetProperties().Where(p => p.GetIndexParameters().Length == 0))
        {
            CollectLeaves(property.GetValue(obj), $"{path}.{property.Name}", leaves, seen);
        }
    }

    private void StubCampReadModel(IReadOnlyList<CampInfo> camps)
    {
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Settings2026()));
        _camps.GetCampsForEventAsync(EventIdFor(2026), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(camps));
    }

    private CampController BuildController(Guid? userId = null, IStringLocalizer<CampsResource>? campsLocalizer = null,
        IStringLocalizer<SharedResource>? sharedLocalizer = null, ILogger<CampController>? logger = null)
    {
        var controller = new CampController(
            _camps,
            _contacts,
            _roles,
            _cityPlanning,
            _shiftView,
            _users,
            _authorization,
            _clock,
            logger ?? NullLogger<CampController>.Instance,
            campsLocalizer ?? _campsLocalizer,
            sharedLocalizer ?? _sharedLocalizer);

        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (userId.HasValue)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())],
                authenticationType: "test"));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = nameof(CampController.Index) }
        };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        return controller;
    }

    /// <summary>2026 is the active event and the only one open for registration.</summary>
    private static CampSettingsInfo Settings2026() => new(2026, [2026])
    {
        ActiveEventSettingsId = Event2026.EventSettingsId,
        Events = [Event2029, Event2027, Event2026],
    };

    private static Guid EventIdFor(int year) =>
        new[] { Event2026, Event2027, Event2029 }.Single(e => e.Year == year).EventSettingsId;

    private static CampInfo MakeCamp(
        string slug,
        string name,
        CampSeasonStatus status,
        Guid? leadUserId = null,
        YesNoMaybe kidsWelcome = YesNoMaybe.Yes,
        IReadOnlyList<CampSeasonMemberInfo>? members = null,
        int year = 2026)
    {
        var campId = Guid.NewGuid();
        var season = new CampSeasonInfo(
            Guid.NewGuid(),
            campId,
            slug,
            year,
            NameLockDate: null,
            name,
            $"{name} short",
            Languages: "en",
            Vibes: [CampVibe.ChillOut],
            status,
            AcceptingMembers: YesNoMaybe.Yes,
            kidsWelcome,
            AdultPlayspacePolicy.No,
            MemberCount: 0,
            SoundZone: SoundZone.Green,
            SpaceRequirement: null,
            ElectricalGrid: null,
            EeSlotCount: 0,
            EeGrantedCount: 0,
            JoinedMemberCount: 0)
        {
            EventSettingsId = EventIdFor(year),
            LeadUserIds = leadUserId.HasValue ? [leadUserId.Value] : [],
            Members = members ?? []
        };

        return new CampInfo(
            campId,
            slug,
            ContactEmail: $"{slug}@example.com",
            ContactPhone: "+34600000000",
            IsSwissCamp: false,
            TimesAtNowhere: 1,
            Seasons: [season]);
    }

    private static CampSeasonMemberInfo MakeSeasonMember(Guid userId) =>
        new(
            Guid.NewGuid(),
            userId,
            CampMemberStatus.Active,
            SystemClock.Instance.GetCurrentInstant(),
            SystemClock.Instance.GetCurrentInstant(),
            HasEarlyEntry: false);

    private static CampEditData MakeEditData(CampInfo camp, CampSeasonInfo season) =>
        new(
            camp.Id,
            camp.Slug,
            season.Id,
            season.Year,
            season.EventSettingsId,
            IsNameLocked: false,
            season.Name,
            camp.ContactEmail,
            camp.ContactPhone,
            Links: [],
            camp.IsSwissCamp,
            camp.HideHistoricalNames,
            camp.TimesAtNowhere,
            season.BlurbLong,
            season.BlurbShort,
            season.Languages,
            season.AcceptingMembers,
            season.KidsWelcome,
            season.KidsVisiting,
            season.KidsAreaDescription,
            season.HasPerformanceSpace,
            season.PerformanceTypes,
            season.Vibes,
            season.AdultPlayspace,
            season.MemberCount,
            season.SpaceRequirement,
            season.SoundZone,
            season.ElectricalGrid,
            Images: [],
            HistoricalNames: []);

    private static ShiftUserSummary MakeShiftUserSummary(Guid userId, params SignupStatus[] statuses) =>
        ShiftFixtures.UserSummary(
            userId,
            [.. statuses.Select(status => ShiftFixtures.Signup(status: status))]);

    private static UserInfo MakeUserInfo(Guid userId) =>
        UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en" },
            [],
            [],
            UserFixtures.Profile(
                burnerName: "Lead Human"));
}
