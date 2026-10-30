namespace Humans.Base.Attributes;

/// <summary>
/// Marks a controller or action a signed-in account reaches before its membership is Active: the onboarding
/// surface, the status wall and its recovery actions, own-profile maintenance. The Shell's membership gate
/// reads it; everything without it, and without <c>[AllowAnonymous]</c>, waits for <c>UserState.Active</c>.
/// A method-level attribute wins over the controller's.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class AllowBeforeActiveMembershipAttribute : Attribute
{
    /// <summary>Also reachable by Deleted and Merged accounts, which otherwise see only the status wall.</summary>
    public bool Terminal { get; init; }
}
