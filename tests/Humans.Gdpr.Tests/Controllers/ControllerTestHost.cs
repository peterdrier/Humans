using System.Security.Claims;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Humans.Gdpr.Tests.Controllers;

/// <summary>Wires a signed-in principal, TempData and logging onto a controller under test.</summary>
internal static class ControllerTestHost
{
    public static T Wire<T>(this T controller, Guid? userId, params Claim[] extraClaims) where T : Controller
    {
        var claims = new List<Claim>(extraClaims);
        if (userId is { } id)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth")),
            RequestServices = services.BuildServiceProvider(),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.Url = Substitute.For<IUrlHelper>();
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    public static UserInfo Info(Guid id, Action<User>? configure = null, ProfileInfo? profile = null)
    {
        var user = new User { Id = id, PreferredLanguage = "en" };
        configure?.Invoke(user);
        return UserInfo.Create(user, [], [], profile);
    }
}
