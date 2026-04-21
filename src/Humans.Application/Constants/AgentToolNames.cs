namespace Humans.Application.Constants;

public static class AgentToolNames
{
    public const string FetchFeatureSpec = "fetch_feature_spec";
    public const string FetchSectionGuide = "fetch_section_guide";
    public const string RouteToFeedback = "route_to_feedback";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            FetchFeatureSpec,
            FetchSectionGuide,
            RouteToFeedback
        };
}
