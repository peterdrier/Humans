using System.Reflection;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Debug.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Localization;

namespace Humans.Debug.Tests;

/// <summary>Architecture tests enforcing the section shape for Debug.</summary>
public class DebugArchitectureTests
{
    [HumansFact]
    public void SectionRegistersNothingOfItsOwn()
    {
        // Anything registered here means Debug has grown a service of its own — worth a
        // second look, not a green build.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        new Section().Register(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        services.Should().BeEmpty();
    }

    [HumansFact]
    public void AdminSurfacesRequireAdminOnly_ExceptTheTwoAnonymousOnes()
    {
        Type[] adminControllers = [typeof(DebugController), typeof(WidgetGalleryController)];

        foreach (var controller in adminControllers)
        {
            var authorize = controller.GetCustomAttribute<AuthorizeAttribute>();
            authorize.Should().NotBeNull(because: $"{controller.Name} is admin-only");
            authorize!.Policy.Should().Be(PolicyNames.AdminOnly, because: $"{controller.Name} is admin-only");
        }

        typeof(ColorPaletteController).GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().NotBeNull(because: "the colour palette is a static design reference");

        var anonymousActions = typeof(DebugController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(m => m.Name);

        anonymousActions.Should().Equal(
            [nameof(DebugController.DbVersion)],
            because: "DbVersion (migration names and counts) is the only anonymous diagnostics endpoint");
    }

    [HumansFact]
    public void OnlyLocalizerBoundIsSharedResource()
    {
        // Debug carries no resource set: every string is English developer copy, and the one
        // localizer it touches is the shared set read as data by /Debug/Translations.
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var boundLocalizers = typeof(Section).Assembly.GetTypes()
            .SelectMany(t =>
                t.GetConstructors(all).SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
                .Concat(t.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType)))
                .Concat(t.GetProperties(all).Select(p => p.PropertyType))
                .Concat(t.GetFields(all).Select(f => f.FieldType)))
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
            .Select(type => type.GetGenericArguments()[0])
            .Distinct()
            .ToList();

        boundLocalizers.Should().Equal([typeof(SharedResource)]);
    }
}
