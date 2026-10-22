namespace Humans.Base.Attributes;

/// <summary>
/// Marks a seam member (a contribution interface or one of its properties) that carries a view component
/// <see cref="Type"/> rather than a string name, and declares the args record the host must pass. <see cref="Args"/>
/// null means every parameter on the component's <c>Invoke</c>/<c>InvokeAsync</c> must be optional; otherwise MVC
/// binds the record's public properties to them by name. <c>ViewComponentSlotContractTests</c> verifies it.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Property)]
public sealed class ViewComponentSlotAttribute(Type? args = null) : Attribute
{
    public Type? Args { get; } = args;
}
