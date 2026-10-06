using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.Containers.Contracts;
using Humans.Containers.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Containers.Tests;

public sealed class ContainerValidationLocalizationTests
{
    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public void FormValidationErrors_AreLocalized(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };

        var validator = services.GetRequiredService<IObjectModelValidator>();
        var shared = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        foreach (var (model, field, key, arguments) in new (ContainerFormModel, string, string, object[])[]
        {
            (new() { Name = "" }, "Name", "Validation_Required", ["Name"]),
            (new() { Name = "<container>" }, "Name", "Validation_InvalidCharacters", ["Name", "[^<>$]*"]),
            (new() { Name = new string('x', 257) }, "Name", "Validation_MaxLength", ["Name", 256]),
            (new() { Name = "Container", Description = new string('x', 2001) }, "Description", "Validation_MaxLength", ["Description", 2000])
        })
        {
            var context = new ActionContext { HttpContext = http };
            validator.Validate(context, null, "", model);
            var expected = shared[key, arguments];
            expected.ResourceNotFound.Should().BeFalse();
            context.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);
        }
    }
}
