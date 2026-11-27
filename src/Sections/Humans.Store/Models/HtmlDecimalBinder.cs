using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Humans.Store.Models;

/// <summary>HTML number inputs submit invariant values even when their form uses another culture.</summary>
internal sealed class HtmlDecimalBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (value == ValueProviderResult.None) return Task.CompletedTask;

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, value);
        var text = value.FirstValue?.Trim();
        if (string.IsNullOrEmpty(text) && bindingContext.ModelMetadata.IsNullableValueType)
            bindingContext.Result = ModelBindingResult.Success(null);
        else if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            bindingContext.Result = ModelBindingResult.Success(amount);
        else
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName,
                bindingContext.ModelMetadata.ModelBindingMessageProvider.AttemptedValueIsInvalidAccessor(
                    value.FirstValue ?? string.Empty, bindingContext.ModelMetadata.GetDisplayName()));

        return Task.CompletedTask;
    }
}
