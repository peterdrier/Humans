namespace Humans.Application.Constants;

public static class AgentToolNames
{
    public const string FetchFeatureSpec = "fetch_feature_spec";
    public const string FetchSectionGuide = "fetch_section_guide";
    public const string GetAuditHistory = "get_audit_history";
    public const string RouteToIssue = "route_to_issue";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            FetchFeatureSpec,
            FetchSectionGuide,
            GetAuditHistory,
            RouteToIssue
        };
}
