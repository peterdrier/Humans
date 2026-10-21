using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.Web.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Middleware;

public sealed class PrivateUploadMiddlewareTests
{
    [HumansTheory]
    [InlineData(null, false)]
    [InlineData("Volunteer", false)]
    [InlineData("FeedbackAdmin", false)]
    [InlineData("Admin", true)]
    public async Task FeedbackScreenshots_RequireTheReportAdminPolicy(string? role, bool allowed)
    {
        var (context, served, _) = await RunAsync("/uploads/feedback/report/screenshot.png", role);
        served.Should().Be(allowed);
        context.Response.StatusCode.Should().Be(allowed ? 200 : 404);
        context.Response.Headers.CacheControl.ToString().Should().Be("private, no-store");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssueScreenshots_UseTheOwnerSectionsViewerScopedRead(bool allowed)
    {
        var (context, served, issues) = await RunAsync($"/uploads/issues/{IssueId}/screenshot.png", "Volunteer", allowed);
        served.Should().Be(allowed);
        context.Response.StatusCode.Should().Be(allowed ? 200 : 404);
        await issues.Received(1).GetIssueByIdAsync(IssueId,
            Arg.Is<IssueViewer>(viewer => viewer.UserId == UserId && viewer.Roles.Contains("Volunteer")), context.RequestAborted);
    }

    [HumansTheory]
    [InlineData("/uploads/profile-pictures/p.png", false)]
    [InlineData("/uploads/camps/p.png", true)]
    [InlineData("/css/site.css", true)]
    public async Task OtherStaticPaths_KeepTheirExistingAccess(string path, bool allowed)
    {
        var (_, served, _) = await RunAsync(path, null);
        served.Should().Be(allowed);
    }

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid IssueId = Guid.NewGuid();

    [HumansTheory]
    [InlineData("//uploads/feedback/report/screenshot.png")]
    [InlineData("/uploads/camps/../feedback/report/screenshot.png")]
    [InlineData("/UPLOADS/FEEDBACK/report/screenshot.png")]
    [InlineData("/uploads/issues")]
    [InlineData("/uploads/issues/not-an-id/screenshot.png")]
    public async Task PrivatePathAliasesAndMalformedIssuePaths_AreDenied(string path)
    {
        var (context, served, _) = await RunAsync(path, "Volunteer");
        served.Should().BeFalse();
        context.Response.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task IssueScreenshot_RejectsOtherFilesEvenWhenTheReportIsVisible()
    {
        var (context, served, _) = await RunAsync($"/uploads/issues/{IssueId}/other.png", "Admin", issueAllowed: true);
        served.Should().BeFalse();
        context.Response.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task IssueScreenshot_RejectsAnonymousRequests()
    {
        var (context, served, issues) = await RunAsync($"/uploads/issues/{IssueId}/screenshot.png", null, issueAllowed: true);
        served.Should().BeFalse();
        context.Response.StatusCode.Should().Be(404);
        await issues.DidNotReceiveWithAnyArgs().GetIssueByIdAsync(default, default!);
    }

    private static async Task<(HttpContext Context, bool Served, IIssueTriage Issues)> RunAsync(
        string path, string? role, bool issueAllowed = false)
    {
        var principal = role is null ? new ClaimsPrincipal() : new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId.ToString()), new Claim(ClaimTypes.Role, role)], "cookie"));
        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>()).Returns(
            role is null ? AuthenticateResult.NoResult() : AuthenticateResult.Success(new AuthenticationTicket(principal, "cookie")));
        var issues = Substitute.For<IIssueTriage>();
        if (issueAllowed)
            issues.GetIssueByIdAsync(IssueId, Arg.Any<IssueViewer>(), Arg.Any<CancellationToken>()).Returns(
                new IssueDetail(IssueId, IssueStatus.Open, IssueCategory.Bug, null, "Title", "Description",
                    null, null, null, $"uploads/issues/{IssueId}/screenshot.png", UserId,
                    null, null, null, null, default, default, null, 0));
        var services = new ServiceCollection().AddLogging().AddSingleton(authentication).AddSingleton(issues);
        services.AddAuthorization(options => options.AddPolicy(PolicyNames.AdminOnly, policy => policy.RequireRole(RoleNames.Admin)));
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = path;
        var served = false;
        await new PrivateUploadMiddleware(_ => { served = true; return Task.CompletedTask; }).InvokeAsync(context);
        return (context, served, issues);
    }
}
