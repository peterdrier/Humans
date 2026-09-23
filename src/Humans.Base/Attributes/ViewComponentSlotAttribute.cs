namespace Humans.Base.Attributes;

/// <summary>
/// Marks a seam member — a contribution interface or one of its properties — that carries a
/// view component <see cref="Type"/> rather than invoking it by string name
/// (nobodies-collective/Humans#1815). It declares the args record the host must pass when it
/// invokes that <see cref="Type"/>: <see cref="Args"/> null means the component takes no
/// required arguments (every parameter on its <c>Invoke</c>/<c>InvokeAsync</c> must be
/// optional); otherwise the host passes an instance of <see cref="Args"/>, whose public
/// properties MVC binds to the component's parameters by name. The universal architecture
/// test <c>ViewComponentSlotContractTests</c> reads this attribute to verify every
/// contributed component's parameters are actually satisfied.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Property)]
public sealed class ViewComponentSlotAttribute(Type? args = null) : Attribute
{
    public Type? Args { get; } = args;
}
