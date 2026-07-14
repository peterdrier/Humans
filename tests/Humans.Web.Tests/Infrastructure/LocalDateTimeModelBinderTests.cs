using System.Globalization;
using AwesomeAssertions;
using Humans.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Primitives;
using NodaTime;
using Xunit;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// MVC model-binding coverage for datetime-local wire values
/// (nobodies-collective/Humans#932). Runs the real binder against a form value
/// provider; the full form-POST path is covered by
/// Humans.Integration.Tests SurveyAdminControllerTests (Docker required).
/// </summary>
public class LocalDateTimeModelBinderTests
{
    [HumansTheory]
    [InlineData("2026-07-14T10:30", 2026, 7, 14, 10, 30, 0)]      // browser datetime-local: no seconds
    [InlineData("2026-07-14T10:30:45", 2026, 7, 14, 10, 30, 45)]  // some pickers include seconds
    public async Task Binds_datetime_local_wire_formats(
        string wireValue, int year, int month, int day, int hour, int minute, int second)
    {
        var context = CreateContext(typeof(LocalDateTime?), wireValue);

        await new LocalDateTimeModelBinder().BindModelAsync(context);

        context.Result.IsModelSet.Should().BeTrue();
        context.Result.Model.Should().Be(new LocalDateTime(year, month, day, hour, minute, second));
        context.ModelState.ErrorCount.Should().Be(0);
    }

    [HumansFact]
    public async Task Empty_value_binds_to_null_for_nullable()
    {
        var context = CreateContext(typeof(LocalDateTime?), "");

        await new LocalDateTimeModelBinder().BindModelAsync(context);

        context.Result.IsModelSet.Should().BeTrue();
        context.Result.Model.Should().BeNull();
        context.ModelState.ErrorCount.Should().Be(0);
    }

    [HumansFact]
    public async Task Unparsable_value_adds_model_error()
    {
        var context = CreateContext(typeof(LocalDateTime?), "not-a-date");

        await new LocalDateTimeModelBinder().BindModelAsync(context);

        context.Result.IsModelSet.Should().BeFalse();
        context.ModelState["OpensAt"]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("not-a-date");
    }

    [HumansTheory]
    [InlineData(typeof(LocalDateTime))]
    [InlineData(typeof(LocalDateTime?))]
    public void Provider_serves_LocalDateTime_models(Type modelType)
    {
        GetBinderFor(modelType).Should().BeOfType<LocalDateTimeModelBinder>();
    }

    [HumansFact]
    public void Provider_leaves_LocalDate_to_its_TypeConverter()
    {
        GetBinderFor(typeof(LocalDate?)).Should().BeNull();
    }

    private static IModelBinder? GetBinderFor(Type modelType)
    {
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(modelType);
        return new LocalDateTimeModelBinderProvider().GetBinder(new TestModelBinderProviderContext(metadata));
    }

    private static DefaultModelBindingContext CreateContext(Type modelType, string wireValue)
    {
        var form = new FormCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["OpensAt"] = wireValue,
        });

        return (DefaultModelBindingContext)DefaultModelBindingContext.CreateBindingContext(
            new ActionContext { HttpContext = new DefaultHttpContext() },
            new FormValueProvider(BindingSource.Form, form, CultureInfo.InvariantCulture),
            new EmptyModelMetadataProvider().GetMetadataForType(modelType),
            bindingInfo: null,
            modelName: "OpensAt");
    }

    private sealed class TestModelBinderProviderContext(ModelMetadata metadata) : ModelBinderProviderContext
    {
        public override BindingInfo BindingInfo { get; } = new();
        public override ModelMetadata Metadata { get; } = metadata;
        public override IModelMetadataProvider MetadataProvider { get; } = new EmptyModelMetadataProvider();
        public override IModelBinder CreateBinder(ModelMetadata metadata) => throw new NotSupportedException();
    }
}
