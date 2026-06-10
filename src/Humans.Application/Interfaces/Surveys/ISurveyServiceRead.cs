namespace Humans.Application.Interfaces.Surveys;

/// <summary>
/// Cross-section read surface for the Survey section. Established as the boundary other sections
/// would inject (per memory/architecture/section-read-write-split.md). Empty in v1 — nothing
/// cross-section consumes Survey yet; methods returning DTOs (never EF entities) are added when a
/// consumer appears.
/// </summary>
public interface ISurveyServiceRead
{
}
