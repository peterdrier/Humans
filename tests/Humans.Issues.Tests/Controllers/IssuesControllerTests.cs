using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Issues.Controllers;
using Humans.Issues.Contracts;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
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
    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public void SubmissionForm_LocalizesRequiredAndLengthErrors(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();

        void AssertErrors(SubmitIssueViewModel model, params (string Field, string Key, object[] Arguments)[] errors)
        {
            var context = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
            validator.Validate(context, null, "", model);
            foreach (var (field, key, arguments) in errors)
            {
                var expected = localizer[key, arguments];
                expected.ResourceNotFound.Should().BeFalse();
                context.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);
            }
        }

        AssertErrors(new SubmitIssueViewModel(),
            ("Title", "Validation_Required", []), ("Description", "Validation_Required", []));
        AssertErrors(new SubmitIssueViewModel
        {
            Title = new string('x', 201),
            Description = new string('x', 5001),
            Section = new string('x', 65),
            PageUrl = new string('x', 2001),
            UserAgent = new string('x', 1001),
            AdditionalContext = new string('x', 2001)
        },
            ("Title", "Validation_MaxLength", ["", 200]),
            ("Description", "Validation_MaxLength", ["", 5000]),
            ("Section", "Validation_MaxLength", ["", 64]),
            ("PageUrl", "Validation_MaxLength", ["", 2000]),
            ("UserAgent", "Validation_MaxLength", ["", 1000]),
            ("AdditionalContext", "Validation_MaxLength", ["", 2000]));
    }

    [HumansTheory]
    [InlineData("status", true)]
    [InlineData("assignee", true)]
    [InlineData("section", true)]
    [InlineData("github", true)]
    [InlineData("status", false)]
    [InlineData("assignee", false)]
    [InlineData("section", false)]
    [InlineData("github", false)]
    public async Task Triage_InvalidBinding_DoesNotMutateOrBypassAuthorization(string field, bool allowed)
    {
        var viewerId = Guid.NewGuid();
        var issueId = Guid.NewGuid();
        var issues = Substitute.For<IIssuesService>();
        issues.GetIssueByIdAsync(issueId, Arg.Any<IssueViewer>(), Arg.Any<CancellationToken>()).Returns(
            new IssueDetail(issueId, IssueStatus.Resolved, default, "Camps", "Existing", "Details",
                null, null, null, null, viewerId, Guid.NewGuid(), viewerId, 42, null, default, default, null, 0));
        issues.UpdateStatusWithResultAsync(issueId, Arg.Any<IssueViewer>(), Arg.Any<IssueStatus>(), viewerId)
            .Returns(IssueMutationResult.Success());
        issues.UpdateAssigneeWithResultAsync(issueId, Arg.Any<IssueViewer>(), Arg.Any<Guid?>(), viewerId)
            .Returns(IssueMutationResult.Success());
        issues.UpdateSectionWithResultAsync(issueId, Arg.Any<IssueViewer>(), Arg.Any<string?>(), viewerId)
            .Returns(IssueMutationResult.Success());
        issues.SetGitHubIssueNumberWithResultAsync(issueId, Arg.Any<IssueViewer>(), Arg.Any<int?>(), viewerId)
            .Returns(IssueMutationResult.Success());
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(viewerId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = viewerId }, [], [], [], null, []));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var localizer = Substitute.For<IStringLocalizer<IssuesResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, viewerId.ToString())], "test"))
        };
        var controller = new IssuesController(issues, authorization, users, new IssueSectionRouting([]),
            localizer, NullLogger<IssuesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };
        controller.ModelState.AddModelError(field, "Malformed input.");
        var result = field switch
        {
            "status" => await controller.UpdateStatus(issueId, new UpdateIssueStatusModel()),
            "assignee" => await controller.UpdateAssignee(issueId, new UpdateIssueAssigneeModel()),
            "section" => await controller.UpdateSection(issueId, new UpdateIssueSectionModel { Section = new string('x', 65) }),
            _ => await controller.SetGitHubIssue(issueId, new SetIssueGitHubIssueModel())
        };

        if (allowed) result.Should().BeOfType<BadRequestObjectResult>();
        else result.Should().BeOfType<ForbidResult>();
        issues.ReceivedCalls().Where(call => call.GetMethodInfo().Name.EndsWith("WithResultAsync", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

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
