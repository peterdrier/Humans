using System.Globalization;
using System.Resources;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AwesomeAssertions;
using Humans.Email.Controllers;
using Humans.Email.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Email.Tests.Controllers;

public class UnsubscribeControllerTests
{
    [HumansTheory]
    [MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task LegacyGreeting_RendersCurrentIdentityWithoutProfileAccess(string language)
    {
        using var culture = new CultureScope(language);
        var userId = Guid.NewGuid();
        var tokens = Substitute.For<IUnsubscribeService>();
        tokens.ValidateTokenAsync("token", Arg.Any<CancellationToken>())
            .Returns(UnsubscribeTokenResult.Valid(userId, MessageCategory.Marketing, isLegacy: true));
        var controller = new UnsubscribeController(tokens, NullLogger<UnsubscribeController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var result = Assert.IsType<ViewResult>(await controller.Index("token"));
        const string currentName = "Renamed <script>alert(1)</script> &";
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId, BurnerName = currentName }, [], [], profile: null));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(UnsubscribeControllerTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(users);
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(EmailResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddViewLocalization();
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var view = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/Unsubscribe/Index.cshtml", isMainPage: false);
        Assert.True(view.Success);
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        using var writer = new StringWriter();
        var context = new ViewContext(action, view.View!, result.ViewData,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await view.View!.RenderAsync(context);
        var html = writer.ToString();
        var greeting = Regex.Match(html, "<p>(?<greeting>.*?)</p>",
            RegexOptions.Singleline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)).Groups["greeting"].Value.Trim();
        var template = new ResourceManager(typeof(EmailResource))
            .GetString("Email_Unsubscribe_Greeting", CultureInfo.GetCultureInfo(language));
        Assert.NotNull(template);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, template,
            $"<strong><span>{HtmlEncoder.Default.Encode(currentName)}</span></strong>"), greeting);
        Assert.DoesNotContain(userId.ToString(), html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-human-popover", html, StringComparison.Ordinal);
    }

    [HumansFact]
    public async Task Index_RequestAborted_CancelsTokenValidation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var service = Substitute.For<IUnsubscribeService>();
        service.ValidateTokenAsync("token", Arg.Any<CancellationToken>()).Returns(UnsubscribeTokenResult.Invalid());
        service.ValidateTokenAsync("token", aborted.Token)
            .Returns(Task.FromException<UnsubscribeTokenResult>(new OperationCanceledException(aborted.Token)));
        var controller = new UnsubscribeController(service, NullLogger<UnsubscribeController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token },
            },
        };

        var read = () => controller.Index("token");

        await read.Should().ThrowAsync<OperationCanceledException>();
    }
}
