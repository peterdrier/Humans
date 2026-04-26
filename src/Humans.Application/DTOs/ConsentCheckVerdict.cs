namespace Humans.Application.DTOs;

/// <summary>
/// Result of an LLM-based Consent Check evaluation. The LLM is asked to answer
/// two yes/no questions: does the legal name look like a plausible real name,
/// and does it match any entry on the hold list. The job uses both answers to
/// decide whether to auto-approve the pending onboarding review.
/// </summary>
/// <param name="PlausibleRealName">
/// True if the legal name looks like a plausible real human name (not an obvious
/// placeholder, not profane, not a celebrity / historical figure, etc.).
/// </param>
/// <param name="HoldListMatch">
/// True if the legal name matches any entry on the supplied hold list (fuzzy
/// match, case-insensitive, tolerant of accent/ordering differences).
/// </param>
/// <param name="Reason">
/// Short explanation the LLM provided. Stored verbatim in the audit log so a
/// human reviewer can understand why the auto-approval did or did not fire.
/// </param>
/// <param name="ModelId">
/// The model identifier reported by the API (e.g. "claude-haiku-4-5-20251001").
/// Captured in the audit trail to make retroactive quality analysis possible
/// when Anthropic releases new Haiku versions.
/// </param>
public sealed record ConsentCheckVerdict(
    bool PlausibleRealName,
    bool HoldListMatch,
    string Reason,
    string ModelId);
