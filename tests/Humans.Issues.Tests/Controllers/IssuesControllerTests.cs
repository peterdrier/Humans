using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Issues.Controllers;
using Humans.Issues.Domain;
using Humans.Issues.Models;
using Humans.Issues.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Issues.Tests.Controllers;

public sealed class IssuesControllerTests
{
    [HumansFact]
    public async Task Index_OrdersTheAdminReporterDropdownAlphabetically()
    {
        var adminId = Guid.NewGuid();
        var issues = Substitute.For<IIssuesService>();
        issues.GetDistinctReportersAsync(Arg.Any<CancellationToken>()).Returns(
            new List<DistinctReporterRow>
            {
                new(Guid.NewGuid(), "Zoe", 2), new(Guid.NewGuid(), "Alice", 1),
            });
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(adminId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = adminId }, [], [], [], null, []));
        var controller = new IssuesController(issues, Substitute.For<IAuthorizationService>(), users,
            new IssueSectionRouting([]), Substitute.For<IStringLocalizer<IssuesResource>>(),
            NullLogger<IssuesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, adminId.ToString()), new Claim(ClaimTypes.Role, RoleNames.Admin)],
                        "test"))
                }
            }
        };

        var result = await controller.Index(null, null, null, null, null, null);

        var model = Assert.IsType<IssuePageViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(new[] { "Alice", "Zoe" }, model.Reporters.Select(r => r.DisplayName), StringComparer.Ordinal);
        Assert.Equal(new[] { 1, 2 }, model.Reporters.Select(r => r.Count));
    }
}
