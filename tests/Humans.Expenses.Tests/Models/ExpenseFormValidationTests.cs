using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Expenses.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Humans.Expenses.Tests.Models;

public sealed class ExpenseFormValidationTests
{
    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public void MemberForms_RenderLocalizedAnnotationErrors(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        services.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IObjectModelValidator>();
        var localizer = provider.GetRequiredService<IStringLocalizer<SharedResource>>();
        var longNote = new string('x', 501);
        var cases = new (object Model, string Field, string Key, object[] Arguments)[]
        {
            (new ExpenseNewViewModel { Note = longNote }, "Note", "Validation_MaxLength", ["Note", 500]),
            (new ExpenseEditViewModel { Note = longNote }, "Note", "Validation_MaxLength", ["Note", 500]),
            (new AddLineInputModel { Description = "", Amount = 1 }, "Description", "Validation_Required", ["Description"]),
            (new AddLineInputModel { Description = longNote, Amount = 1 }, "Description", "Validation_MaxLength", ["Description", 500]),
            (new EditLineInputModel { Description = "", Amount = 1 }, "Description", "Validation_Required", ["Description"]),
            (new EditLineInputModel { Description = longNote, Amount = 1 }, "Description", "Validation_MaxLength", ["Description", 500]),
            (new AddLineInputModel { Description = "Receipt", Amount = 0 }, "Amount", "Validation_Range", ["Amount", 0.01, 1_000_000]),
            (new EditLineInputModel { Description = "Receipt", Amount = 0 }, "Amount", "Validation_Range", ["Amount", 0.01, 1_000_000]),
            (new ExpenseIbanViewModel { Iban = new string('x', 35) }, "Iban", "Validation_MaxLength", ["Iban", 34]),
        };
        foreach (var (model, field, key, arguments) in cases)
        {
            var context = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = provider } };
            validator.Validate(context, null, "", model);

            var expected = localizer[key, arguments];
            expected.ResourceNotFound.Should().BeFalse($"{key} must exist in {culture}");
            context.ModelState[field]!.Errors.Should().ContainSingle()
                .Which.ErrorMessage.Should().Be(expected.Value);
        }
    }
}
