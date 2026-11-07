using Humans.Tickets.Contracts;
using Humans.Finance.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Grants.Authorization;
using Humans.Grants.Contracts;
using Humans.Grants.Services;
using Humans.Grants.Controllers;
using Humans.Grants.Models;
using Humans.Grants.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Grants.Tests.Authorization;

/// <summary>Who may do what, including the deny paths the section doc lists.</summary>
public sealed class AuthorizationHandlerTests : GrantsTestHarness
{
    private static ClaimsPrincipal Principal(Guid userId, params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)).Prepend(new Claim(ClaimTypes.NameIdentifier, userId.ToString())),
            authenticationType: "test"));

    private async Task<bool> ProgramAllowedAsync(ClaimsPrincipal user, ProgramInfo program, ProgramOperationRequirement requirement)
    {
        var context = new AuthorizationHandlerContext([requirement], user, program);
        await new ProgramAuthorizationHandler(Users, new Microsoft.AspNetCore.Http.HttpContextAccessor()).HandleAsync(context);
        return context.HasSucceeded;
    }

    private async Task<bool> PolicyAllowedAsync(ClaimsPrincipal user)
    {
        var context = new AuthorizationHandlerContext([new ManagesGrantsRequirement()], user, resource: null);
        await new GrantsPolicyHandler(NewService(), Users, new Microsoft.AspNetCore.Http.HttpContextAccessor()).HandleAsync(context);
        return context.HasSucceeded;
    }

    [HumansTheory]
    [Xunit.InlineData("PolicyViewer")]
    [Xunit.InlineData("ProgramViewer")]
    [Xunit.InlineData("Manager")]
    public async Task AuthorizationReads_PropagateAbandonedRequests(string boundary)
    {
        using var request = new CancellationTokenSource();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } };
        var userId = SeedUser("Manager");
        var viewer = await Users.GetUserInfoAsync(userId, Ct);
        var program = (await NewService().GetProgramAsync((await SeedProgramAsync(managers: userId)).Id, Ct))!;
        var grants = Substitute.For<IGrantManagement>();
        var observed = CancellationToken.None;
        void Abandon(CancellationToken ct)
        {
            observed = ct;
            request.Cancel();
            ct.ThrowIfCancellationRequested();
        }
        Users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            var ct = call.Arg<CancellationToken>();
            if (!string.Equals(boundary, "Manager", StringComparison.Ordinal)) Abandon(ct);
            ct.ThrowIfCancellationRequested();
            return new ValueTask<Humans.Users.Contracts.UserInfo?>(viewer);
        });
        grants.ManagesAnyProgramAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Abandon(call.Arg<CancellationToken>());
            return true;
        });
        var context = string.Equals(boundary, "ProgramViewer", StringComparison.Ordinal)
            ? new AuthorizationHandlerContext([ProgramOperationRequirement.Manage], Principal(userId), program)
            : new AuthorizationHandlerContext([new ManagesGrantsRequirement()], Principal(userId), null);
        IAuthorizationHandler handler = string.Equals(boundary, "ProgramViewer", StringComparison.Ordinal)
            ? new ProgramAuthorizationHandler(Users, http)
            : new GrantsPolicyHandler(grants, Users, http);
        Func<Task> authorize = () => handler.HandleAsync(context);
        await authorize.Should().ThrowAsync<OperationCanceledException>();
        observed.Should().Be(request.Token);
        context.HasSucceeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task ManagerOfA_ManagesA_NotB_NeverAdministers()
    {
        var manager = SeedUser("Manager");
        var a = (await NewService().GetProgramAsync((await SeedProgramAsync(name: "A", managers: manager)).Id, Ct))!;
        var b = (await NewService().GetProgramAsync((await SeedProgramAsync(name: "B")).Id, Ct))!;
        var user = Principal(manager);

        (await ProgramAllowedAsync(user, a, ProgramOperationRequirement.View)).Should().BeTrue();
        (await ProgramAllowedAsync(user, a, ProgramOperationRequirement.Manage)).Should().BeTrue();
        (await ProgramAllowedAsync(user, b, ProgramOperationRequirement.View)).Should().BeFalse();
        (await ProgramAllowedAsync(user, b, ProgramOperationRequirement.Manage)).Should().BeFalse();
        (await ProgramAllowedAsync(user, a, ProgramOperationRequirement.Administer)).Should().BeFalse();
        (await PolicyAllowedAsync(user)).Should().BeTrue();
    }

    [HumansTheory]
    [Xunit.InlineData(RoleNames.Admin)]
    [Xunit.InlineData(GrantsRoles.GrantsAdmin)]
    public async Task AdminAndGrantsAdmin_ManageAndAdministerEveryProgram(string role)
    {
        var program = (await NewService().GetProgramAsync((await SeedProgramAsync()).Id, Ct))!;
        var user = Principal(SeedUser("Role holder"), role);

        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.View)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Manage)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Administer)).Should().BeTrue();
        (await PolicyAllowedAsync(user)).Should().BeTrue();
    }

    [HumansFact]
    public async Task Board_ViewsEveryProgram_ButNeitherManagesNorAdministers()
    {
        var program = (await NewService().GetProgramAsync((await SeedProgramAsync()).Id, Ct))!;
        var user = Principal(SeedUser("Board member"), RoleNames.Board);

        (await PolicyAllowedAsync(user)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.View)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Manage)).Should().BeFalse();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Administer)).Should().BeFalse();
    }

    [HumansTheory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData(FinanceRoles.FinanceAdmin)]
    [Xunit.InlineData(TicketsRoles.TicketsAdmin)]
    public async Task OtherPeople_AreDenied_EvenGrantHolders(string? role)
    {
        var person = SeedUser("Holder");
        var program = await SeedProgramAsync();
        await SeedGrantAsync(program, person);
        var info = (await NewService().GetProgramAsync(program.Id, Ct))!;
        var user = role is null ? Principal(person) : Principal(person, role);

        (await PolicyAllowedAsync(user)).Should().BeFalse();
        (await ProgramAllowedAsync(user, info, ProgramOperationRequirement.View)).Should().BeFalse();
        (await ProgramAllowedAsync(user, info, ProgramOperationRequirement.Manage)).Should().BeFalse();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Index_ShowsProgramsManagedThroughAnyMergedId_AndHidesUnrelatedPrograms(bool archivedCookie)
    {
        var archived = Guid.NewGuid();
        var survivor = SeedUser("Survivor", mergedIds: [archived]);
        AliasMergedId(archived, survivor);
        var throughAlias = await SeedProgramAsync(name: "Archived manager", managers: archived);
        var throughSurvivor = await SeedProgramAsync(name: "Current manager", managers: survivor);
        await SeedProgramAsync(name: "Other manager", managers: SeedUser("Other"));
        var viewer = Principal(archivedCookie ? archived : survivor);
        var service = NewService();
        var program = (await service.GetProgramAsync(throughAlias.Id, Ct))!;
        (await PolicyAllowedAsync(viewer)).Should().BeTrue();
        (await ProgramAllowedAsync(viewer, program, ProgramOperationRequirement.Manage)).Should().BeTrue();
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        var controller = new GrantsAdminController(service, Users, authorization, Settings,
            NullLogger<GrantsAdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = viewer } }
        };

        var result = await controller.Index(Ct);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<AdminIndexViewModel>().Subject;
        model.CanAdminister.Should().BeFalse();
        model.Programs.Select(p => p.Id).Should().BeEquivalentTo([throughAlias.Id, throughSurvivor.Id]);
    }

    [HumansFact]
    public async Task ManagerOfA_ActingOnBsGrantThroughAsRoute_IsNotFound_AndChangesNothing()
    {
        var manager = SeedUser("Manager");
        var a = await SeedProgramAsync(name: "A", managers: manager);
        var grantOfB = await SeedGrantAsync(await SeedProgramAsync(name: "B"), SeedUser("Holder"));
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => ((ISectionPolicies)new Section()).AddPolicies(options))
            .AddSingleton<IAuthorizationHandler>(new ProgramAuthorizationHandler(Users, new HttpContextAccessor()))
            .BuildServiceProvider();
        var service = NewService();
        var controller = new GrantsAdminController(service, Users, provider.GetRequiredService<IAuthorizationService>(), Settings,
            NullLogger<GrantsAdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(manager) } }
        };

        (await controller.Revoke(a.Id, grantOfB.Id, "no", Ct)).Should().BeOfType<NotFoundResult>();
        (await controller.Resend(a.Id, grantOfB.Id, Ct)).Should().BeOfType<NotFoundResult>();
        (await service.GetGrantsForUserAsync(grantOfB.UserId, Ct)).Single().RevokedAt.Should().BeNull();
    }

    [HumansFact]
    public async Task ACookieStillCarryingAMergedAwayId_ManagesTheSurvivorsProgram()
    {
        var archived = Guid.NewGuid();
        var survivor = SeedUser("Survivor", mergedIds: [archived]);
        AliasMergedId(archived, survivor);
        var program = (await NewService().GetProgramAsync((await SeedProgramAsync(managers: survivor)).Id, Ct))!;
        var user = Principal(archived);

        (await PolicyAllowedAsync(user)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Manage)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Administer)).Should().BeFalse();
    }
}
