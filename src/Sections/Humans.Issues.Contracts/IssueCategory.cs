namespace Humans.Issues.Contracts;

/// <summary>
/// What kind of issue this is. Stored as string in DB.
/// </summary>
/// <remarks>
/// On the contracts leaf rather than the section's internal <c>Domain/</c> because
/// <c>Humans.Agent</c>'s <c>route_to_issue</c> proposal names it (design §15 step 5).
/// Persisted with <c>HasConversion&lt;string&gt;()</c>, so the member names are data; there
/// is no <c>EnumStringStabilityTests</c> row for it (see the G5 findings for Issues).
/// </remarks>
public enum IssueCategory
{
    Bug,
    Feature,
    Question
}
