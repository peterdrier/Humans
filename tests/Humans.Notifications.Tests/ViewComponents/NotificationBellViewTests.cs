using System.Globalization;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Notifications.Models;
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
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(NotificationBellViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(NotificationsResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/Shared/Components/NotificationBell/Default.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
        {
            Model = new NotificationBadgeViewModel { ActionableUnreadCount = 2 },
        };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());

        await result.View!.RenderAsync(context);

        writer.ToString().Should().Contain($"data-load-error=\"{HtmlEncoder.Default.Encode(message)}\"");
    }
}
