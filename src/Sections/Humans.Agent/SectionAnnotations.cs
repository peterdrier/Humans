using Humans.Agent.Contracts;
using Humans.Agent.Services.Preload;
using Humans.Base.Interfaces;

namespace Humans.Agent;

/// <summary>
/// Lets <c>/Debug/Sections</c> show where the assistant answers from first-party docs. Canonical keys only: an alias
/// is a spelling, not a section with a doc. Operator-only sections are absent on purpose (see
/// <see cref="AgentSectionKeys"/>).
/// </summary>
internal sealed class SectionAnnotations : ISectionAnnotations
{
    public IEnumerable<SectionAnnotation> Annotations() =>
        AgentSectionKeys.All.Select(key => new SectionAnnotation(
            key,
            "Agent doc key",
            $"{AgentSectionDocReader.SectionProjectFolder(key)}/{key}.md"));
}
