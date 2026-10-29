namespace Humans.Base.Authorization;

/// <summary>
/// Who a requirement that is not a plain role check admits, for the access pill. A role check needs nothing: the pill
/// reads <c>RolesAuthorizationRequirement</c> itself.
/// </summary>
public interface IDescribedRequirement
{
    /// <summary>Role names, shown by their titles, or plain labels such as "Team Coordinator".</summary>
    IReadOnlyList<string> Admits { get; }
}
