namespace Humans.Agent.Services;

internal static class AgentToolNames
{
    public const string FetchCommunityFaq = "fetch_community_faq";
    public const string FetchFeatureSpec = "fetch_feature_spec";
    public const string FetchSectionDoc = "fetch_section_doc";
    public const string GetAuditHistory = "get_audit_history";
    public const string GetShiftDetails = "get_shift_details";
    public const string Handoff = "handoff";

    /// <summary>The handoff marker before the rename. Stored rows keep it until retention purges them.</summary>
    public const string LegacyHandoff = "route_to_issue";

    public static bool IsHandoffMarker(string doc) =>
        string.Equals(doc, Handoff, StringComparison.Ordinal)
        || string.Equals(doc, LegacyHandoff, StringComparison.Ordinal);

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            FetchCommunityFaq,
            FetchFeatureSpec,
            FetchSectionDoc,
            GetAuditHistory,
            GetShiftDetails,
            Handoff
        };
}
