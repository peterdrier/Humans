namespace Humans.Application.Architecture;

/// <summary>
/// Marks a class that legitimately holds another section's <b>write</b> service
/// interface. Cross-section calls default to the read surface
/// (<c>I&lt;Section&gt;ServiceRead</c>); this is the declared exception.
/// </summary>
/// <remarks>
/// Consumed by HUM0032. Prefer pulling in the owning section's UI component and letting
/// it do its own writing — reach for this only when that is not possible.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CrossSectionWriteAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
