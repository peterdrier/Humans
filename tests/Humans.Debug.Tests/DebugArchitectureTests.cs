using System.Reflection;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Debug.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Debug.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Debug. The anonymous
/// <c>/api/data-migrations</c> endpoint is a minimal-API map, not a controller action, so it is
/// pinned by <see cref="DataMigrationsTests"/> rather than here.
/// </summary>
public class DebugArchitectureTests
{
    [HumansFact]
    public void AdminSurfacesAreBoardReadable_AndEveryWriteIsAdminOnly_ExceptTheDeliberateAnonymousOnes()
    {
        // Discovered from the assembly, not listed: a controller added later enters this
        // assertion by existing, which is the only way the invariant survives new surfaces.
        var controllers = typeof(Section).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        var anonymousControllers = controllers
            .Where(t => t.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .ToList();

        anonymousControllers.Should().BeEquivalentTo(
            [typeof(ColorPaletteController)],
            because: "the colour palette is the section's one anonymous page — a static design reference");

        var adminControllers = controllers.Except(anonymousControllers).ToList();

        foreach (var controller in adminControllers)
        {
            var authorize = controller.GetCustomAttribute<AuthorizeAttribute>(inherit: false);
            authorize.Should().NotBeNull(because: $"{controller.Name} is operator tooling");
            authorize.Policy.Should().Be(PolicyNames.BoardOrAdmin, because: $"Board reads {controller.Name}, Admin acts");

            // Board sees everything and changes nothing: every POST carries its own AdminOnly.
            foreach (var post in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null))
            {
                var postAuthorize = post.GetCustomAttribute<AuthorizeAttribute>(inherit: false);
                postAuthorize.Should().NotBeNull(because: $"{controller.Name}.{post.Name} changes state");
                postAuthorize.Policy.Should().Be(PolicyNames.AdminOnly, because: $"{controller.Name}.{post.Name} changes state");
            }
        }

        var anonymousActions = adminControllers
            .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(m => m.Name);

        anonymousActions.Should().BeEquivalentTo(
            [nameof(DebugController.DbVersion)],
            because: "DbVersion (migration names and counts) is the only anonymous action on an admin controller");
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
