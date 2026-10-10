using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Base.Tests.ViewComponents;

public class HumanNameLocalizationTests
{
    [HumansTheory]
    [InlineData("en", "Unknown")]
    [InlineData("es", "Desconocido")]
    [InlineData("de", "Unbekannt")]
    [InlineData("it", "Sconosciuto")]
    [InlineData("fr", "Inconnu")]
    [InlineData("ca", "Desconegut")]
    public async Task MissingNames_AreLocalized_WithoutRewritingARealName(string cultureName, string expected)
    {
        using var culture = new CultureScope(cultureName);
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var users = Substitute.For<IUserServiceRead>();
        var component = new HumanViewComponent(users, Substitute.For<IUrlHelperFactory>(),
            services.GetRequiredService<IStringLocalizer<SharedResource>>())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
        var userId = Guid.NewGuid();
        var nameless = UserInfo.Create(new User { Id = userId, BurnerName = "", PreferredLanguage = "en" },
            [], [], [], null, []);

        foreach (var id in new[] { Guid.Empty, userId })
        {
            var result = (ViewViewComponentResult)await component.InvokeAsync(id, link: HumanLink.None);
            ((HumanViewModel)result.ViewData!.Model!).DisplayName.Should().Be(expected);
        }
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(nameless);
        var emptyName = (ViewViewComponentResult)await component.InvokeAsync(userId, link: HumanLink.None);
        ((HumanViewModel)emptyName.ViewData!.Model!).DisplayName.Should().Be(expected);

        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(nameless with { BurnerName = "Unknown" });
        var realName = (ViewViewComponentResult)await component.InvokeAsync(userId, link: HumanLink.None);
        ((HumanViewModel)realName.ViewData!.Model!).DisplayName.Should().Be("Unknown");
    }
}
