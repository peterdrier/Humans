namespace Humans.Application.Constants;

/// <summary>Display names for legal documents that callers compare against
/// (consent gates, name-based lookups). Compared with <c>StringComparison.Ordinal</c>
/// — these are stable identifiers, not user-visible strings.</summary>
public static class LegalDocumentNames
{
    public const string AgentChatTerms = "Agent Chat Terms";
}
