using System.Net;
using System.Text.RegularExpressions;
using Humans.Base.Extensions;
using Humans.Users.Contracts;
using Humans.Users.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.ViewComponents;

public class CommunicationPreferenceAccessibilityTests
{
    [HumansTheory]
    [InlineData("en", "Member")]
    [InlineData("es", "Member")]
    [InlineData("de", "Member")]
    [InlineData("it", "Member")]
    [InlineData("fr", "Member")]
    [InlineData("ca", "Member")]
    [InlineData("en", "Admin")]
    [InlineData("es", "Admin")]
    [InlineData("de", "Admin")]
    [InlineData("it", "Admin")]
    [InlineData("fr", "Admin")]
    [InlineData("ca", "Admin")]
    [InlineData("en", "Guest")]
    [InlineData("es", "Guest")]
    [InlineData("de", "Guest")]
    [InlineData("it", "Guest")]
    [InlineData("fr", "Guest")]
    [InlineData("ca", "Guest")]
    public async Task PreferenceCheckboxes_HaveLocalizedChannelAndCategoryNames(string culture, string surface)
    {
        using var cultureScope = new CultureScope(culture);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(CommunicationPreferenceAccessibilityTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(UsersResource).Assembly);
        builder.Services.AddSingleton<IClock>(new FakeClock(Instant.FromUtc(2026, 5, 20, 0, 0)));
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var guest = string.Equals(surface, "Guest", StringComparison.Ordinal);
        var model = new CommunicationPreferencesViewModel
        {
            ReadOnly = string.Equals(surface, "Admin", StringComparison.Ordinal),
            TicketingYear = 2026,
            Categories =
            [
                new() { Category = MessageCategory.Marketing, EmailEnabled = false, AlertEnabled = false, EmailEditable = true, AlertEditable = true },
                new() { Category = MessageCategory.System, EmailEditable = false, AlertEditable = false },
                new() { Category = MessageCategory.Ticketing, EmailEditable = false, AlertEditable = false, Note = "Locked — you have a ticket for this year" },
            ],
        };
        var engine = services.GetRequiredService<IRazorViewEngine>();
        var path = guest ? "/Views/GuestAccount/CommunicationPreferences.cshtml"
            : "/Views/Shared/Components/CommunicationPreferencesPanel/Default.cshtml";
        var result = engine.GetView(null, path, isMainPage: false);
        Assert.True(result.Success, string.Join(", ", result.SearchedLocations ?? []));
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary<CommunicationPreferencesViewModel>(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter();
        var viewContext = new ViewContext(action, result.View, data, new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View.RenderAsync(viewContext);
        var html = writer.ToString();
        var localizer = services.GetRequiredService<IStringLocalizer<UsersResource>>();
        var labels = Regex.Matches(html, "<input\\b[^>]*type=\"checkbox\"[^>]*>", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1))
            .Select(input => WebUtility.HtmlDecode(Regex.Match(input.Value, "aria-label=\"(?<label>[^\"]+)\"", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)).Groups["label"].Value)).ToArray();
        var expected = model.Categories.SelectMany(item =>
        {
            var name = item.Category == MessageCategory.Ticketing ? localizer["CommPrefs_TicketingYear", 2026].Value : localizer[$"CommPrefs_Name_{item.Category}"].Value;
            var email = $"{localizer["CommPrefs_Email"].Value} — {name}";
            return guest ? new[] { email, $"{localizer["CommPrefs_Alert"].Value} — {name}" } : [email];
        }).ToArray();
        Assert.Equal(expected, labels);
    }
}
