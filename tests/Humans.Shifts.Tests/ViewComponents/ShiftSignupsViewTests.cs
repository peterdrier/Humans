using Humans.Base.Enums;
using System.Globalization;
using System.Resources;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
using System.Text.Encodings.Web;
using Humans.Users.Domain;
using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Humans.Users.Contracts;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Humans.Shifts.Tests.ViewComponents;

public class ShiftSignupsViewTests
{
    [HumansFact]
    public async Task Header_resolves_the_subject_only_for_admin_mode()
    {
        using var culture = new CultureScope(CultureCatalog.DefaultCultureCode);
        var userId = Guid.NewGuid();
        const string name = "Current <script>&";
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId, BurnerName = name }, [], [], null));
        var title = new ResourceManager(typeof(ShiftsResource))
            .GetString("Shifts_ShiftSignups_Title", CultureInfo.CurrentUICulture)!;
        var admin = await RenderAsync(new ShiftSignupsViewModel { UserId = userId, ViewMode = ProfileCardViewMode.Admin }, users);
        admin.Should().Contain(HtmlEncoder.Default.Encode(name)).And.Contain(HtmlEncoder.Default.Encode(title))
            .And.NotContain("<script>");
        users.ClearReceivedCalls();
        var self = await RenderAsync(new ShiftSignupsViewModel { UserId = userId, ViewMode = ProfileCardViewMode.Self }, users);
        self.Should().Contain(HtmlEncoder.Default.Encode(title)).And.NotContain(HtmlEncoder.Default.Encode(name));
        await users.DidNotReceive().GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static async Task<string> RenderAsync(ShiftSignupsViewModel model, IUserService users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ShiftSignupsViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(users);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddApplicationPart(typeof(ShiftsResource).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/Shared/Components/ShiftSignups/Default.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary());
        data.Model = model;
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }
}
