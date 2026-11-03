using Humans.Base.Extensions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.ModelBinders;

/// <summary>
/// Binds NodaTime <see cref="LocalDateTime"/> from <c>&lt;input type="datetime-local"&gt;</c> and
/// <see cref="LocalTime"/> from <c>&lt;input type="time"&gt;</c>. Browsers post both without seconds
/// (<c>2026-07-14T10:30</c>, <c>10:30</c>), and NodaTime's <c>TypeConverter</c> requires them, so every non-empty
/// submit would otherwise fail binding. Accepts both, plus fractions.
/// </summary>
public sealed class LocalDateTimeModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var valueResult = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (valueResult == ValueProviderResult.None)
            return Task.CompletedTask;

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, valueResult);

        var value = valueResult.FirstValue;
        if (string.IsNullOrWhiteSpace(value))
        {
            // Mirror SimpleTypeModelBinder: empty binds to null for nullable models,
            // and is a model error for non-nullable ones.
            if (bindingContext.ModelMetadata.IsReferenceOrNullableType)
            {
                bindingContext.Result = ModelBindingResult.Success(null);
            }
            else
            {
                bindingContext.ModelState.TryAddModelError(
                    bindingContext.ModelName,
                    bindingContext.ModelMetadata.ModelBindingMessageProvider
                        .ValueMustNotBeNullAccessor(valueResult.ToString()));
            }
            return Task.CompletedTask;
        }

        // No-seconds wire format first (what browsers actually post), then ISO with
        // seconds (and optional fractions) for pickers/browsers that include them.
        var parsed = IsTime(bindingContext.ModelType) ? ParseTime(value) : ParseDateTime(value);

        if (parsed is not null)
        {
            bindingContext.Result = ModelBindingResult.Success(parsed);
        }
        else
        {
            bindingContext.ModelState.TryAddModelError(
                bindingContext.ModelName,
                bindingContext.ModelMetadata.ModelBindingMessageProvider
                    .AttemptedValueIsInvalidAccessor(
                        value,
                        bindingContext.ModelMetadata.DisplayName ?? bindingContext.ModelName));
        }

        return Task.CompletedTask;
    }

    private static bool IsTime(Type modelType) => (Nullable.GetUnderlyingType(modelType) ?? modelType) == typeof(LocalTime);

    private static object? ParseDateTime(string value)
    {
        var parsed = DateFormattingExtensions.PlacementDateTimePattern.Parse(value);
        if (!parsed.Success)
            parsed = LocalDateTimePattern.ExtendedIso.Parse(value);
        return parsed.Success ? parsed.Value : null;
    }

    private static object? ParseTime(string value)
    {
        var parsed = DateFormattingExtensions.TimeOfDayPattern.Parse(value);
        if (!parsed.Success)
            parsed = LocalTimePattern.ExtendedIso.Parse(value);
        return parsed.Success ? parsed.Value : null;
    }
}

/// <summary>Serves <see cref="LocalDateTimeModelBinder"/> for <c>LocalDateTime</c> and <c>LocalTime</c> models,
/// nullable or not. Registered ahead of the default providers in Program.cs.</summary>
public sealed class LocalDateTimeModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var modelType = context.Metadata.ModelType;
        var underlying = Nullable.GetUnderlyingType(modelType) ?? modelType;
        return underlying == typeof(LocalDateTime) || underlying == typeof(LocalTime) ? new LocalDateTimeModelBinder() : null;
    }
}
