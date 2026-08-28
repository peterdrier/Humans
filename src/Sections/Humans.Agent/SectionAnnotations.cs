using Humans.Agent.Contracts;
using Humans.Agent.Services.Preload;
using Humans.Base.Interfaces;

namespace Humans.Agent;

/// <summary>
/// Publishes which sections the agent can fetch a grounding doc for, so <c>/Debug/Sections</c>
/// shows at a glance where the assistant answers from first-party docs and where it falls back
/// to the community FAQ (nobodies-collective/Humans#1509).
/// </summary>
/// <remarks>
/// Only the canonical keys, not the aliases: an alias is a spelling the model uses, not a
/// section that has a doc. A canonical key naming no section is real drift and surfaces as an
/// unmatched annotation. The path comes from <see cref="AgentSectionDocReader"/> rather than a
/// literal: a section keeps its invariants doc inside its own project, and
/// <c>docs/sections</c> — which the reader still probes first — holds only the templates. Operator-only sections are absent on purpose — see
/// <see cref="AgentSectionKeys"/>; the catalog is the oracle for what a section *is*, never for
/// which subset the agent should serve.
/// </remarks>
internal sealed class SectionAnnotations : ISectionAnnotations
{
    public IEnumerable<SectionAnnotation> Annotations() =>
        AgentSectionKeys.All.Select(key => new SectionAnnotation(
            key,
            "Agent doc key",
            $"{AgentSectionDocReader.SectionProjectFolder(key)}/{key}.md"));
}
