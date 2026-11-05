using System.Globalization;
using System.Resources;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.GoogleIntegration.Models;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

public sealed class GoogleIdentityViewTests
{
    [HumansFact]
    public async Task SyncAndRenames_RenderLocalIdentity_AndKeepExternalNames()
    {
        using var culture = new CultureScope("en");
        var known = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(known, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = known, BurnerName = "Current <b>&" }, [], [], null));
        var sync = new SyncTabContentViewModel
        {
            ResourceType = "DriveFolder",
            Result = new SyncPreviewResult
            {
                Diffs = [new ResourceSyncDiff
                {
                    ResourceId = Guid.NewGuid(), ResourceName = "Folder", PermissionLevel = "reader",
                    Members = [
                        new MemberSyncStatus("known@example.com", "", MemberSyncState.Downgrade, [], "writer", "reader", known),
                        new MemberSyncStatus("missing@example.com", "missing@example.com", MemberSyncState.Downgrade, [], "writer", "reader", missing),
                        new MemberSyncStatus("external@example.com", "External <b>&", MemberSyncState.Downgrade, [], "writer", "reader"),
                    ],
                }],
            },
        };
        var html = await RenderAsync("_SyncTabContent", sync, users);
        var unknown = new ResourceManager(typeof(SharedResource)).GetString("Common_Unknown", CultureInfo.CurrentUICulture)!;
        var detailedRows = html[html.IndexOf("class=\"member-row\"", StringComparison.Ordinal)..];
        foreach (var markup in new[] { html, detailedRows })
        {
            markup.Should().Contain(HtmlEncoder.Default.Encode("Current <b>&"));
            markup.Should().Contain(HtmlEncoder.Default.Encode(unknown));
            markup.Should().Contain(HtmlEncoder.Default.Encode("External <b>&"));
            markup.Should().NotContain("Current <b>&").And.NotContain("External <b>&");
        }
        html.Should().Contain("known@example.com").And.Contain("writer").And.Contain("reader");
        var renames = new EmailRenameDetectionResult
        {
            TotalUsersChecked = 2,
            Renames = [
                new EmailRenameInfo { UserId = known, OldEmail = "old@example.com", NewEmail = "new@example.com", AffectedResourceCount = 2 },
                new EmailRenameInfo { UserId = missing, OldEmail = "missing-old@example.com", NewEmail = "missing-new@example.com" },
            ],
        };
        var renameHtml = await RenderAsync("EmailRenames", renames, users);
        renameHtml.Should().Contain(HtmlEncoder.Default.Encode("Current <b>&")).And.Contain(HtmlEncoder.Default.Encode(unknown));
        renameHtml.Should().Contain("old@example.com").And.Contain("new@example.com");
        renameHtml.Should().NotContain("Current <b>&");
    }

    private static async Task<string> RenderAsync(string page, object model, IUserService users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GoogleIdentityViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(Humans.Base.Authorization.PolicyNames.AdminOnly, policy => policy.RequireAssertion(_ => false));
            options.AddPolicy(Humans.Base.Authorization.PolicyNames.BoardOrAdmin, policy => policy.RequireAssertion(_ => false));
        });
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(GoogleIntegrationResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly);
        builder.Services.AddSingleton(users);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, $"/Views/Google/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Google";
        route.Values["action"] = page;
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }
}
