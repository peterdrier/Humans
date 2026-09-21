using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Testing;
using Humans.Workgroups.Controllers;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Models;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Workgroups.Tests.Controllers;

public sealed class WorkgroupsAdminControllerTests : WorkgroupsTestHarness
{
    [HumansTheory]
    [InlineData(nameof(WorkgroupsAdminController.Refuse))]
    [InlineData(nameof(WorkgroupsAdminController.Withdraw))]
    [InlineData(nameof(WorkgroupsAdminController.Close))]
    public async Task Invalid_reasons_render_queue_with_original_text_and_error(string action)
    {
        var status = string.Equals(action, nameof(WorkgroupsAdminController.Refuse), StringComparison.Ordinal)
            ? WorkgroupStatus.Applied : WorkgroupStatus.Active;
        var workgroup = await SeedWorkgroupAsync(status: status);
        var actor = SeedUser("Board member");
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, actor.ToString()),
                new Claim(ClaimTypes.Role, RoleNames.Board)], "test"))
        };
        var localizer = Substitute.For<IStringLocalizer<WorkgroupsResource>>();
        localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(call => new LocalizedString(call.Arg<string>(), "Use 4000 characters or fewer."));
        var sut = new WorkgroupsAdminController(NewService(), Users, localizer, Clock,
            NullLogger<WorkgroupsAdminController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = http,
                ActionDescriptor = new ControllerActionDescriptor { ActionName = action }
            },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };
        var reasons = new string('x', 4001);

        var result = action switch
        {
            nameof(WorkgroupsAdminController.Refuse) => await sut.Refuse(workgroup.Id, reasons, Ct),
            nameof(WorkgroupsAdminController.Withdraw) => await sut.Withdraw(workgroup.Id, reasons, Ct),
            _ => await sut.Close(workgroup.Id, reasons, Ct)
        };

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be(nameof(WorkgroupsAdminController.Index));
        view.Model.Should().BeOfType<AdminQueueViewModel>().Subject.Register
            .Should().ContainSingle().Which.Id.Should().Be(workgroup.Id);
        view.ViewData[$"Reasons:{workgroup.Id}:{action}"].Should().Be(reasons);
        sut.ModelState.IsValid.Should().BeFalse();
        sut.ModelState[string.Empty]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("4000");
        sut.TempData.Should().BeEmpty("oversized text must not enter the TempData cookie");
        await using var db = OpenContext();
        (await db.Workgroups.FindAsync([workgroup.Id], Ct))!.Status.Should().Be(status);
    }
}
