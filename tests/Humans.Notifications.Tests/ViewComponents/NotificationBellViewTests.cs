using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Notifications.Models;
using Humans.Notifications.Services;
using Humans.Users.Contracts;
using NodaTime;
using NodaTime.Testing;
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

namespace Humans.Notifications.Tests.ViewComponents;

public class NotificationBellViewTests
{
    [HumansTheory]
    [Xunit.InlineData("en", "Could not load notifications. Please try again.")]
    [Xunit.InlineData("es", "No se pudieron cargar las notificaciones. Inténtalo de nuevo.")]
    [Xunit.InlineData("de", "Benachrichtigungen konnten nicht geladen werden. Bitte versuche es erneut.")]
    [Xunit.InlineData("it", "Impossibile caricare le notifiche. Riprova.")]
    [Xunit.InlineData("fr", "Impossible de charger les notifications. Réessaie.")]
    [Xunit.InlineData("ca", "No s’han pogut carregar les notificacions. Torna-ho a provar.")]
    public async Task Bell_SuppliesLocalizedPopupFailureText(string language, string message)
    {
        using var culture = new CultureScope(language);
        var html = await RenderAsync("/Views/Shared/Components/NotificationBell/Default.cshtml",
            new NotificationBadgeViewModel { ActionableUnreadCount = 2 });

        html.Should().Contain($"data-load-error=\"{HtmlEncoder.Default.Encode(message)}\"");
    }

    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    public async Task ResolvedRow_RendersTheHumanInsideTheLocalizedMessage(string language)
    {
        using var culture = new CultureScope(language);
        var resolverId = Guid.NewGuid();
        const string name = "Resolver <script>&";
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(resolverId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = resolverId, BurnerName = name }.ToUserInfo());
        var now = Instant.FromUtc(2026, 11, 3, 1, 0);
        var html = await RenderAsync("/Views/Notifications/_NotificationRow.cshtml",
            new NotificationRowDto
            {
                Id = Guid.NewGuid(),
                Title = "Resolved",
                IsResolved = true,
                CreatedAt = now.ToDateTimeUtc(),
                ResolvedAt = now.ToDateTimeUtc(),
                ResolvedByUserId = resolverId,
            }, services =>
            {
                services.AddSingleton(users);
                services.AddSingleton<IClock>(new FakeClock(now));
            });

        var resources = new ResourceManager(typeof(NotificationsResource));
        var expected = string.Format(CultureInfo.CurrentCulture,
            resources.GetString("Notifications_HandledBy", CultureInfo.CurrentUICulture)!, name,
            resources.GetString("Notifications_TimeAgo_JustNow", CultureInfo.CurrentUICulture)!);
        WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", string.Empty, RegexOptions.NonBacktracking)).Should().Contain(expected);
        html.Should().Contain(HtmlEncoder.Default.Encode(name));
        html.Should().NotContain("<script>").And.NotContain("<a ");
    }

    private static async Task<string> RenderAsync(
        string path, object model, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(NotificationBellViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(NotificationsResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddViewLocalization();
        configure?.Invoke(builder.Services);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, path, isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
        {
            Model = model,
        };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());

        await result.View!.RenderAsync(context);

        return writer.ToString();
    }
}
