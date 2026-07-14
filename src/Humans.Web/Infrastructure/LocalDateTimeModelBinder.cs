using Humans.Application.Extensions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Infrastructure;

/// <summary>
/// Binds NodaTime <see cref="LocalDateTime"/> (and <c>LocalDateTime?</c>) from
/// <c>&lt;input type="datetime-local"&gt;</c> form posts. Browsers post the wire value
/// WITHOUT seconds (<c>2026-07-14T10:30</c>) unless the user's picker happens to include
/// them, while NodaTime's built-in <c>TypeConverter</c> (used by MVC's
/// <c>SimpleTypeModelBinder</c>) requires seconds — so without this binder every
/// non-empty datetime-local submit fails model binding (nobodies-collective/Humans#932).
/// Accepts both <c>yyyy-MM-ddTHH:mm</c> and ISO with seconds/fractions.
/// <c>LocalDate</c> is untouched: its TypeConverter parses <c>yyyy-MM-dd</c> fine.
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
        var parsed = DateFormattingExtensions.PlacementDateTimePattern.Parse(value);
        if (!parsed.Success)
            parsed = LocalDateTimePattern.ExtendedIso.Parse(value);

        if (parsed.Success)
        {
            bindingContext.Result = ModelBindingResult.Success(parsed.Value);
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
}

/// <summary>Serves <see cref="LocalDateTimeModelBinder"/> for <c>LocalDateTime</c> and
/// <c>LocalDateTime?</c> models. Registered ahead of the default providers in Program.cs.</summary>
public sealed class LocalDateTimeModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var modelType = context.Metadata.ModelType;
        var underlying = Nullable.GetUnderlyingType(modelType) ?? modelType;
        return underlying == typeof(LocalDateTime) ? new LocalDateTimeModelBinder() : null;
    }
}
