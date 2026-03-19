using Humans.Domain.Enums;

namespace Humans.Domain.ValueObjects;

public class CallToAction
{
    public string Text { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public CallToActionStyle Style { get; set; } = CallToActionStyle.Secondary;
}
